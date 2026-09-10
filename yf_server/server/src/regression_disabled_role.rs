//! 禁用内部用户保留禁用角色的资料维护契约；仅在临时 MySQL 数据库执行。
use crate::{
    entity::{enums::CommonStatus, roles, user_roles, users},
    error::AppError,
    regression::Fixture,
    service,
};
use sea_orm::{ColumnTrait, EntityTrait, QueryFilter};

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn disabled_user_can_keep_disabled_role_while_updating_profile() {
    let f = Fixture::new().await;
    let retired_role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("停用用户保留角色-{}", uuid::Uuid::new_v4().simple()),
            description: None,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    service::user::assign_roles(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::RoleAssign {
            role_ids: vec![retired_role],
        },
    )
    .await
    .unwrap();
    service::user::set_status(&f.state.db, &f.admin, f.member.id, "DISABLED")
        .await
        .unwrap();
    service::role::set_status(&f.state.db, &f.admin, retired_role, "DISABLED")
        .await
        .unwrap();

    let role_options = service::user::role_options(
        &f.state.db,
        &service::user::RoleOptionQuery { keyword: None },
    )
    .await
    .unwrap();
    assert!(!role_options
        .as_array()
        .unwrap()
        .iter()
        .any(|role| role["id"] == retired_role));
    let binding_before = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.eq(f.member.id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(binding_before.len(), 1);
    assert_eq!(binding_before[0].role_id, retired_role);

    let updated = service::user::update(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::UserUpdate {
            real_name: Some("停用账号资料已更新".into()),
            email: None,
            department_id: None,
            role_id: Some(retired_role),
            role_ids: None,
        },
    )
    .await
    .expect("禁用用户保留原禁用角色时应可修改资料");
    assert_eq!(updated["realName"], "停用账号资料已更新");
    assert_eq!(updated["roleId"], retired_role);
    let bindings = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.eq(f.member.id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(bindings, binding_before);

    let updated_without_role_metadata = service::user::update(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::UserUpdate {
            real_name: Some("停用账号再次更新".into()),
            email: None,
            department_id: None,
            role_id: None,
            role_ids: None,
        },
    )
    .await
    .expect("禁用用户省略角色字段并保留原禁用角色时应可修改资料");
    assert_eq!(
        updated_without_role_metadata["realName"],
        "停用账号再次更新"
    );
    assert_eq!(updated_without_role_metadata["roleId"], retired_role);
    let bindings_without_role_metadata = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.eq(f.member.id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(bindings_without_role_metadata, binding_before);

    assert!(matches!(
        service::user::set_status(&f.state.db, &f.admin, f.member.id, "ACTIVE").await,
        Err(AppError::BadRequest(_))
    ));
    assert_eq!(
        users::Entity::find_by_id(f.member.id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        CommonStatus::Disabled
    );

    let another_disabled_role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("不可改绑禁用角色-{}", uuid::Uuid::new_v4().simple()),
            description: None,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    service::role::set_status(&f.state.db, &f.admin, another_disabled_role, "DISABLED")
        .await
        .unwrap();
    assert!(matches!(
        service::user::update(
            &f.state.db,
            &f.admin,
            f.member.id,
            &service::user::UserUpdate {
                real_name: None,
                email: None,
                department_id: None,
                role_id: Some(another_disabled_role),
                role_ids: None,
            },
        )
        .await,
        Err(AppError::BadRequest(_))
    ));

    let active_role = roles::Entity::find()
        .filter(roles::Column::Name.eq("内部成员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    service::user::update(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::UserUpdate {
            real_name: None,
            email: None,
            department_id: None,
            role_id: Some(active_role.id),
            role_ids: None,
        },
    )
    .await
    .unwrap();
    service::user::set_status(&f.state.db, &f.admin, f.member.id, "ACTIVE")
        .await
        .unwrap();
}
