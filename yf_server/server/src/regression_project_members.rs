//! Project member read/write contract regressions. Run only through scripts/test-isolated.py.

use crate::{
    entity::{enums::UserType, projects, roles, users},
    error::AppError,
    middleware::auth::CurrentUser,
    regression::Fixture,
    service,
};
use sea_orm::{ColumnTrait, EntityTrait, QueryFilter};
use serde_json::Value;

fn rows(value: &Value) -> &[Value] {
    value.as_array().expect("成员接口应返回数组")
}

async fn create_supplier_account(f: &Fixture, supplier_id: u64, real_name: &str) -> u64 {
    let employee_no = format!("s{}", &uuid::Uuid::new_v4().simple().to_string()[..24]);
    let email = format!("{employee_no}@example.invalid");
    service::supplier::create_account(
        &f.state.db,
        &f.admin,
        supplier_id,
        &service::supplier::AccountCreate {
            employee_no,
            password: "Regression123".into(),
            real_name: real_name.into(),
            email,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap()
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_project_members_keep_internal_contract_and_department_name() {
    let f = Fixture::new().await;
    let department = service::dept::create(
        &f.state.db,
        &f.admin,
        &service::dept::DeptUpsert {
            name: "成员回归部门".into(),
            parent_id: None,
            sort_no: Some(10),
        },
    )
    .await
    .unwrap();
    let department_id = department["id"].as_u64().unwrap();
    service::user::update(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::UserUpdate {
            real_name: None,
            email: None,
            department_id: Some(Some(department_id)),
            role_id: None,
            role_ids: None,
        },
    )
    .await
    .unwrap();

    let supplier_id = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .supplier_id;
    let supplier_user_id = create_supplier_account(&f, supplier_id, "供应商写入拒绝").await;

    let members = service::project::list_members(&f.state.db, f.project_id)
        .await
        .unwrap();
    let members = rows(&members);
    let member = members
        .iter()
        .find(|item| item["userId"] == f.member.id)
        .expect("项目内部成员应可见");
    assert_eq!(member["departmentId"], department_id);
    assert_eq!(member["deptName"], "成员回归部门");
    for item in members {
        let user = users::Entity::find_by_id(item["userId"].as_u64().unwrap())
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(user.user_type, UserType::Internal);
    }

    assert!(service::project::set_members(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::MembersSet {
            user_ids: vec![supplier_user_id],
        },
    )
    .await
    .is_err());
    let after_rejected_put = service::project::list_members(&f.state.db, f.project_id)
        .await
        .unwrap();
    assert!(rows(&after_rejected_put)
        .iter()
        .all(|item| item["userId"] != supplier_user_id));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_supplier_member_view_filters_supplier_and_account_status() {
    let f = Fixture::new().await;
    let project = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let active = create_supplier_account(&f, project.supplier_id, "同供应商启用").await;
    let disabled_account =
        create_supplier_account(&f, project.supplier_id, "同供应商停用账号").await;
    service::supplier::set_account_status(&f.state.db, &f.admin, disabled_account, "DISABLED")
        .await
        .unwrap();

    let other_supplier = service::supplier::create(
        &f.state.db,
        &f.admin,
        &service::supplier::SupplierUpsert {
            name: "其他回归供应商".into(),
            remark: None,
        },
    )
    .await
    .unwrap();
    let other_supplier_id = other_supplier["id"].as_u64().unwrap();
    let other_active = create_supplier_account(&f, other_supplier_id, "其他供应商启用").await;

    let listed = service::project::list_supplier_members(&f.state.db, &f.admin, f.project_id)
        .await
        .unwrap();
    let listed = rows(&listed);
    assert_eq!(listed.len(), 1);
    assert_eq!(listed[0]["userId"], active);
    assert_eq!(listed[0]["status"], "ACTIVE");
    assert_ne!(listed[0]["userId"], disabled_account);
    assert_ne!(listed[0]["userId"], other_active);
    let fields = listed[0]
        .as_object()
        .unwrap()
        .keys()
        .map(String::as_str)
        .collect::<std::collections::HashSet<_>>();
    assert_eq!(
        fields,
        ["employeeNo", "realName", "status", "userId"]
            .into_iter()
            .collect()
    );
    assert!(!listed[0].as_object().unwrap().contains_key("email"));

    let outsider_value = service::user::create(
        &f.state.db,
        &f.admin,
        &service::user::UserCreate {
            employee_no: format!("o{}", &uuid::Uuid::new_v4().simple().to_string()[..24]),
            password: "Regression123".into(),
            real_name: "非项目内部成员".into(),
            email: "outsider@example.invalid".into(),
            department_id: None,
            role_id: Some(
                roles::Entity::find()
                    .filter(roles::Column::Name.eq("内部成员"))
                    .one(&f.state.db)
                    .await
                    .unwrap()
                    .unwrap()
                    .id,
            ),
            role_ids: None,
        },
    )
    .await
    .unwrap();
    let outsider = CurrentUser {
        id: outsider_value["id"].as_u64().unwrap(),
        employee_no: outsider_value["employeeNo"].as_str().unwrap().into(),
        user_type: UserType::Internal,
        supplier_id: None,
    };
    assert!(matches!(
        crate::handler::project::list_project_supplier_members(
            axum::extract::State(f.state.clone()),
            outsider,
            axum::extract::Path(f.project_id),
        )
        .await,
        Err(AppError::OutOfScope)
    ));

    service::supplier::set_status(&f.state.db, &f.admin, project.supplier_id, "DISABLED")
        .await
        .unwrap();
    assert!(rows(
        &service::project::list_supplier_members(&f.state.db, &f.admin, f.project_id)
            .await
            .unwrap()
    )
    .is_empty());
}
