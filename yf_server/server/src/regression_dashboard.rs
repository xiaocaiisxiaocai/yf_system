//! Dashboard counts and pending project lists use the same role and scope rules.
use crate::{
    dto::PageQuery,
    entity::{
        enums::{ConfirmSide, ProjectStatus, UserType},
        projects,
    },
    middleware::auth::CurrentUser,
    regression::Fixture,
    service,
};
use sea_orm::{ActiveModelTrait, EntityTrait, Set};

async fn set_state(f: &Fixture, id: u64, status: ProjectStatus, side: Option<ConfirmSide>) {
    let mut project: projects::ActiveModel = projects::Entity::find_by_id(id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .into();
    project.status = Set(status);
    project.confirm_side = Set(side);
    // Equal timestamps deliberately exercise the stable ID tiebreaker.
    project.updated_at = Set(chrono::DateTime::from_timestamp(1_700_000_000, 0).unwrap());
    project.update(&f.state.db).await.unwrap();
}

async fn add_pending(f: &Fixture, side: ConfirmSide) -> u64 {
    let supplier_id = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .supplier_id;
    let created = service::project::create(
        &f.state.db,
        &f.admin,
        &service::project::ProjectUpsert {
            name: format!("工作台-{}", uuid::Uuid::new_v4()),
            description: None,
            supplier_id,
        },
    )
    .await
    .unwrap();
    let id = created["id"].as_u64().unwrap();
    service::project::set_members(
        &f.state.db,
        &f.admin,
        id,
        &service::project::MembersSet {
            user_ids: vec![f.member.id],
        },
    )
    .await
    .unwrap();
    set_state(f, id, ProjectStatus::PendingConfirmation, Some(side)).await;
    id
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn dashboard_pending_projects_are_paginated_and_match_side_scope_and_permission() {
    let f = Fixture::new().await;
    let foreign = Fixture::new().await;
    set_state(
        &f,
        f.project_id,
        ProjectStatus::PendingConfirmation,
        Some(ConfirmSide::Company),
    )
    .await;
    let second = add_pending(&f, ConfirmSide::Company).await;
    let supplier_pending = add_pending(&f, ConfirmSide::Supplier).await;
    add_pending(&foreign, ConfirmSide::Company).await;
    add_pending(&foreign, ConfirmSide::Supplier).await;
    let db = &f.state.db;

    let first = service::dashboard::pending_projects(
        db,
        &f.member,
        &PageQuery {
            page: 1,
            page_size: 1,
        },
    )
    .await
    .unwrap();
    assert_eq!(first.total, 2);
    assert_eq!(first.list.len(), 1);
    assert_eq!(first.list[0]["id"], second);
    assert_eq!(first.list[0]["status"], "PENDING_CONFIRMATION");
    assert_eq!(first.list[0]["confirmSide"], "COMPANY");
    let next = service::dashboard::pending_projects(
        db,
        &f.member,
        &PageQuery {
            page: 2,
            page_size: 1,
        },
    )
    .await
    .unwrap();
    assert_eq!(next.list[0]["id"], f.project_id);
    let summary = service::dashboard::summary(db, &f.member).await.unwrap();
    assert_eq!(summary["projectCount"], 3);
    assert_eq!(summary["pendingConfirmations"], first.total);

    let project = projects::Entity::find_by_id(f.project_id)
        .one(db)
        .await
        .unwrap()
        .unwrap();
    let created = service::supplier::create_account(
        db,
        &f.admin,
        project.supplier_id,
        &service::supplier::AccountCreate {
            employee_no: format!("s{}", &uuid::Uuid::new_v4().simple().to_string()[..20]),
            password: "Regression123".into(),
            real_name: "工作台供应商".into(),
            email: "dashboard@example.invalid".into(),
        },
    )
    .await
    .unwrap();
    let supplier = CurrentUser {
        id: created["id"].as_u64().unwrap(),
        employee_no: created["employeeNo"].as_str().unwrap().into(),
        user_type: UserType::Supplier,
        supplier_id: Some(project.supplier_id),
    };
    let supplier_page = service::dashboard::pending_projects(
        db,
        &supplier,
        &PageQuery {
            page: 1,
            page_size: 10,
        },
    )
    .await
    .unwrap();
    assert_eq!(supplier_page.total, 1);
    assert_eq!(supplier_page.list[0]["id"], supplier_pending);

    let role = service::role::create(
        db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("无审批-{}", f.member.id),
            description: None,
        },
    )
    .await
    .unwrap();
    service::user::assign_roles(
        db,
        &f.admin,
        f.member.id,
        &service::user::RoleAssign {
            role_ids: vec![role["id"].as_u64().unwrap()],
        },
    )
    .await
    .unwrap();
    let without_permission = service::dashboard::pending_projects(
        db,
        &f.member,
        &PageQuery {
            page: 1,
            page_size: 10,
        },
    )
    .await
    .unwrap();
    assert_eq!(without_permission.total, 0);
    assert!(without_permission.list.is_empty());
    assert_eq!(
        service::dashboard::summary(db, &f.member).await.unwrap()["pendingConfirmations"],
        0
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn dashboard_in_progress_count_does_not_include_pending_confirmation() {
    let f = Fixture::new().await;
    add_pending(&f, ConfirmSide::Company).await;
    let summary = service::dashboard::summary(&f.state.db, &f.member)
        .await
        .unwrap();
    assert_eq!(summary["projectCount"], 2);
    assert_eq!(summary["activeProjectCount"], 1);
    assert_eq!(summary["pendingConfirmations"], 1);
}
