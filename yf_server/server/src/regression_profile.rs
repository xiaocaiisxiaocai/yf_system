//! 本人资料与供应商账号维护边界；由 scripts/test-isolated.py 在临时 MySQL 数据库中执行。
use crate::{
    dto::{ChangePasswordRequest, UpdateProfileRequest},
    entity::{projects, users},
    error::AppError,
    middleware::auth::CurrentUser,
    regression::Fixture,
    service,
};
use sea_orm::EntityTrait;

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn supplier_updates_only_own_email_and_password() {
    let f = Fixture::new().await;
    let supplier_id = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .supplier_id;
    let own = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        supplier_id,
        &service::supplier::AccountCreate {
            employee_no: format!("sp{}", &uuid::Uuid::new_v4().simple().to_string()[..20]),
            password: "Regression123".into(),
            real_name: "供应商本人".into(),
            email: "self-before@example.invalid".into(),
        },
    )
    .await
    .unwrap();
    let other = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        supplier_id,
        &service::supplier::AccountCreate {
            employee_no: format!("sp{}", &uuid::Uuid::new_v4().simple().to_string()[..20]),
            password: "Regression123".into(),
            real_name: "供应商他人".into(),
            email: "other-before@example.invalid".into(),
        },
    )
    .await
    .unwrap();
    let current = CurrentUser {
        id: own["id"].as_u64().unwrap(),
        employee_no: own["employeeNo"].as_str().unwrap().into(),
        user_type: crate::entity::enums::UserType::Supplier,
        supplier_id: Some(supplier_id),
    };

    let updated = service::auth::update_profile(
        &f.state.db,
        &current,
        &UpdateProfileRequest {
            email: " self-after@example.invalid ".into(),
        },
    )
    .await
    .unwrap();
    assert_eq!(updated.user.id, current.id);
    assert_eq!(updated.user.email, "self-after@example.invalid");
    let other_row = users::Entity::find_by_id(other["id"].as_u64().unwrap())
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(other_row.email, "other-before@example.invalid");

    service::auth::change_password(
        &f.state.db,
        &current,
        &ChangePasswordRequest {
            old_password: "Regression123".into(),
            new_password: "Changed456789".into(),
        },
    )
    .await
    .unwrap();
    let own_row = users::Entity::find_by_id(current.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert!(crate::util::password::verify(
        "Changed456789",
        &own_row.password_hash
    ));
    assert!(!own_row.must_change_password);
    assert!(crate::util::password::verify(
        "Regression123",
        &other_row.password_hash
    ));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn supplier_cannot_manage_any_supplier_account() {
    let f = Fixture::new().await;
    let supplier_id = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .supplier_id;
    let own = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        supplier_id,
        &service::supplier::AccountCreate {
            employee_no: format!("sp{}", &uuid::Uuid::new_v4().simple().to_string()[..20]),
            password: "Regression123".into(),
            real_name: "越权测试本人".into(),
            email: "boundary-self@example.invalid".into(),
        },
    )
    .await
    .unwrap();
    let other = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        supplier_id,
        &service::supplier::AccountCreate {
            employee_no: format!("sp{}", &uuid::Uuid::new_v4().simple().to_string()[..20]),
            password: "Regression123".into(),
            real_name: "越权测试他人".into(),
            email: "boundary-other@example.invalid".into(),
        },
    )
    .await
    .unwrap();
    let supplier = CurrentUser {
        id: own["id"].as_u64().unwrap(),
        employee_no: own["employeeNo"].as_str().unwrap().into(),
        user_type: crate::entity::enums::UserType::Supplier,
        supplier_id: Some(supplier_id),
    };
    let other_id = other["id"].as_u64().unwrap();

    assert!(matches!(
        service::supplier::list_accounts(&f.state.db, &supplier, supplier_id).await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::supplier::update_account(
            &f.state.db,
            &supplier,
            other_id,
            &service::supplier::AccountUpdate {
                real_name: None,
                email: Some("stolen@example.invalid".into()),
            },
        )
        .await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::supplier::reset_account_password(&f.state.db, &supplier, other_id, "Stolen456",)
            .await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::supplier::set_account_status(&f.state.db, &supplier, other_id, "DISABLED").await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::supplier::delete_account(&f.state.db, &supplier, other_id).await,
        Err(AppError::Forbidden)
    ));

    let unchanged = users::Entity::find_by_id(other_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(unchanged.email, "boundary-other@example.invalid");
    assert!(crate::util::password::verify(
        "Regression123",
        &unchanged.password_hash
    ));
}
