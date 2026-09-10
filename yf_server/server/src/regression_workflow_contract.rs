//! V1.1 项目审批状态与写入冻结契约。
//!
//! 这些测试必须由 `scripts/test-isolated.py` 在临时 MySQL 数据库中执行。

use chrono::{Duration, Utc};
use sea_orm::{
    ActiveModelTrait, ColumnTrait, EntityTrait, PaginatorTrait, QueryFilter, QueryOrder, Set,
};

use crate::{
    entity::{
        audit_logs, email_outbox,
        enums::{FileDirection, FileStatus, ProjectStatus, UploadStatus, UserType},
        files, messages, project_activities, project_members, project_status_logs, projects, roles,
        upload_sessions,
    },
    error::{ApiResult, AppError},
    middleware::auth::CurrentUser,
    regression::Fixture,
    service,
};

#[derive(Debug, PartialEq, Eq)]
struct ProjectSnapshot {
    project: projects::Model,
    status_logs: Vec<project_status_logs::Model>,
    outbox: Vec<email_outbox::Model>,
    files: Vec<files::Model>,
    uploads: Vec<upload_sessions::Model>,
    members: Vec<project_members::Model>,
    messages: Vec<messages::Model>,
    activities: Vec<project_activities::Model>,
    project_audit_count: u64,
}

async fn snapshot(f: &Fixture) -> ProjectSnapshot {
    let db = &f.state.db;
    let project = projects::Entity::find_by_id(f.project_id)
        .one(db)
        .await
        .unwrap()
        .unwrap();
    let status_logs = project_status_logs::Entity::find()
        .filter(project_status_logs::Column::ProjectId.eq(f.project_id))
        .order_by_asc(project_status_logs::Column::Id)
        .all(db)
        .await
        .unwrap();
    let outbox = email_outbox::Entity::find()
        .filter(email_outbox::Column::ProjectId.eq(f.project_id))
        .order_by_asc(email_outbox::Column::Id)
        .all(db)
        .await
        .unwrap();
    let files = files::Entity::find()
        .filter(files::Column::ProjectId.eq(f.project_id))
        .order_by_asc(files::Column::Id)
        .all(db)
        .await
        .unwrap();
    let uploads = upload_sessions::Entity::find()
        .filter(upload_sessions::Column::ProjectId.eq(f.project_id))
        .order_by_asc(upload_sessions::Column::Id)
        .all(db)
        .await
        .unwrap();
    let members = project_members::Entity::find()
        .filter(project_members::Column::ProjectId.eq(f.project_id))
        .order_by_asc(project_members::Column::UserId)
        .all(db)
        .await
        .unwrap();
    let messages = messages::Entity::find()
        .filter(messages::Column::ProjectId.eq(f.project_id))
        .order_by_asc(messages::Column::Id)
        .all(db)
        .await
        .unwrap();
    let activities = project_activities::Entity::find()
        .filter(project_activities::Column::ProjectId.eq(f.project_id))
        .order_by_asc(project_activities::Column::Id)
        .all(db)
        .await
        .unwrap();
    let project_audit_count = audit_logs::Entity::find()
        .filter(audit_logs::Column::TargetType.eq("project"))
        .filter(audit_logs::Column::TargetId.eq(f.project_id.to_string()))
        .count(db)
        .await
        .unwrap();
    ProjectSnapshot {
        project,
        status_logs,
        outbox,
        files,
        uploads,
        members,
        messages,
        activities,
        project_audit_count,
    }
}

async fn assert_rejected_without_mutation<T>(
    f: &Fixture,
    before: &ProjectSnapshot,
    result: ApiResult<T>,
    label: &str,
) {
    assert!(
        matches!(result, Err(AppError::Conflict(_)) | Err(AppError::BadRequest(_))),
        "{label} must reject the business transition, not fail through authorization or infrastructure"
    );
    assert_eq!(
        before,
        &snapshot(f).await,
        "{label} changed project state or related records"
    );
}

async fn insert_available_file(f: &Fixture, name: &str) -> u64 {
    let stored_name = uuid::Uuid::new_v4().to_string();
    let model = files::ActiveModel {
        project_id: Set(f.project_id),
        uploader_id: Set(f.member.id),
        direction: Set(FileDirection::C2s),
        original_name: Set(name.to_owned()),
        stored_name: Set(stored_name.clone()),
        ext: Set(name
            .rsplit_once('.')
            .map(|(_, ext)| ext.to_ascii_lowercase())
            .unwrap_or_default()),
        size_bytes: Set(4),
        mime_type: Set(Some("application/pdf".into())),
        sha256: Set(None),
        storage_path: Set(format!("regression/{stored_name}")),
        status: Set(FileStatus::Available),
        deleted_at: Set(None),
        created_at: Set(Utc::now()),
        ..Default::default()
    }
    .insert(&f.state.db)
    .await
    .unwrap();
    model.id
}

async fn insert_upload_session(f: &Fixture, status: UploadStatus, name: &str) -> String {
    let id = uuid::Uuid::new_v4().to_string();
    let now = Utc::now();
    let temp_dir = std::path::Path::new(&f.state.cfg.storage.root)
        .join("tmp")
        .join(&id)
        .to_string_lossy()
        .into_owned();
    upload_sessions::ActiveModel {
        id: Set(id.clone()),
        project_id: Set(f.project_id),
        uploader_id: Set(f.member.id),
        file_name: Set(name.to_owned()),
        file_size: Set(4),
        file_md5: Set(None),
        chunk_size: Set(256 * 1024),
        total_chunks: Set(1),
        temp_dir: Set(temp_dir),
        status: Set(status),
        result_file_id: Set(None),
        expires_at: Set(now + Duration::hours(1)),
        created_at: Set(now),
        updated_at: Set(now),
    }
    .insert(&f.state.db)
    .await
    .unwrap();
    id
}

async fn create_internal_user(f: &Fixture, role_id: u64, label: &str) -> CurrentUser {
    let employee_no = format!(
        "w{}{}",
        label,
        &uuid::Uuid::new_v4().simple().to_string()[..16]
    );
    let email = format!("{employee_no}@example.invalid");
    let row = service::user::create(
        &f.state.db,
        &f.admin,
        &service::user::UserCreate {
            employee_no: employee_no.clone(),
            password: "Regression123".into(),
            real_name: format!("流程测试{label}"),
            email,
            department_id: None,
            role_id: Some(role_id),
            role_ids: None,
        },
    )
    .await
    .unwrap();
    CurrentUser {
        id: row["id"].as_u64().unwrap(),
        employee_no,
        user_type: UserType::Internal,
        supplier_id: None,
    }
}

async fn create_internal_with_permissions(f: &Fixture, codes: &[&str], label: &str) -> CurrentUser {
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("流程契约-{label}-{}", uuid::Uuid::new_v4().simple()),
            description: None,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    let permissions = crate::entity::permissions::Entity::find()
        .filter(crate::entity::permissions::Column::Code.is_in(codes.iter().copied()))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(permissions.len(), codes.len(), "流程测试所需权限点缺失");
    service::role::assign_permissions(
        &f.state.db,
        &f.admin,
        role,
        &service::role::PermAssign {
            permission_ids: permissions.iter().map(|permission| permission.id).collect(),
        },
    )
    .await
    .unwrap();
    create_internal_user(f, role, label).await
}

async fn supplier_user(f: &Fixture) -> CurrentUser {
    let project = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let account = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        project.supplier_id,
        &service::supplier::AccountCreate {
            employee_no: format!("s{}", &uuid::Uuid::new_v4().simple().to_string()[..20]),
            password: "Regression123".into(),
            real_name: "流程契约供应商".into(),
            email: format!("supplier-{}@example.invalid", uuid::Uuid::new_v4()),
        },
    )
    .await
    .unwrap();
    CurrentUser {
        id: account["id"].as_u64().unwrap(),
        employee_no: account["employeeNo"].as_str().unwrap().into(),
        user_type: UserType::Supplier,
        supplier_id: Some(project.supplier_id),
    }
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn workflow_submit_requires_available_file_and_no_active_uploads() {
    let f = Fixture::new().await;
    let before = snapshot(&f).await;
    assert!(matches!(
        service::project::submit(
            &f.state.db,
            "http://localhost",
            &f.admin,
            f.project_id,
            &service::project::SubmitReq {
                confirm_side: "SUPPLIER".into(),
            },
        )
        .await,
        Err(AppError::Conflict(_))
    ));
    assert_eq!(before, snapshot(&f).await);

    for (status, name) in [
        (UploadStatus::Uploading, "contract-uploading.pdf"),
        (UploadStatus::Merging, "contract-merging.pdf"),
    ] {
        let f = Fixture::new().await;
        insert_available_file(&f, "contract-available.pdf").await;
        insert_upload_session(&f, status, name).await;
        let before = snapshot(&f).await;
        let result = service::project::submit(
            &f.state.db,
            "http://localhost",
            &f.admin,
            f.project_id,
            &service::project::SubmitReq {
                confirm_side: "SUPPLIER".into(),
            },
        )
        .await;
        assert!(matches!(result, Err(AppError::Conflict(_))));
        assert_eq!(before, snapshot(&f).await, "活动上传不应产生任何提交副作用");
    }
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn workflow_pending_freezes_project_members_files_and_upload_stages_but_allows_message() {
    let f = Fixture::new().await;
    let extra_role = roles::Entity::find()
        .filter(roles::Column::Name.eq("内部成员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let extra = create_internal_user(&f, extra_role.id, "pending").await;
    let file_id = insert_available_file(&f, "pending-existing.pdf").await;
    service::project::submit(
        &f.state.db,
        "http://localhost",
        &f.admin,
        f.project_id,
        &service::project::SubmitReq {
            confirm_side: "SUPPLIER".into(),
        },
    )
    .await
    .unwrap();
    let before = snapshot(&f).await;
    let project = before.project.clone();

    assert_rejected_without_mutation(
        &f,
        &before,
        service::project::update(
            &f.state.db,
            &f.admin,
            f.project_id,
            &service::project::ProjectUpsert {
                name: format!("{}-blocked", project.name),
                description: Some("资料不应修改".into()),
                supplier_id: project.supplier_id,
            },
        )
        .await,
        "project::update(PENDING_CONFIRMATION)",
    )
    .await;
    assert_rejected_without_mutation(
        &f,
        &before,
        service::project::set_members(
            &f.state.db,
            &f.admin,
            f.project_id,
            &service::project::MembersSet {
                user_ids: vec![f.member.id, extra.id],
            },
        )
        .await,
        "project::set_members(add, PENDING_CONFIRMATION)",
    )
    .await;
    assert_rejected_without_mutation(
        &f,
        &before,
        service::project::set_members(
            &f.state.db,
            &f.admin,
            f.project_id,
            &service::project::MembersSet {
                user_ids: vec![f.member.id],
            },
        )
        .await,
        "project::set_members(remove, PENDING_CONFIRMATION)",
    )
    .await;
    assert_rejected_without_mutation(
        &f,
        &before,
        service::file::delete(&f.state, &f.admin, file_id).await,
        "file::delete(PENDING_CONFIRMATION)",
    )
    .await;
    assert_rejected_without_mutation(
        &f,
        &before,
        service::upload::init(
            &f.state,
            &f.member,
            &service::upload::InitReq {
                project_id: f.project_id,
                file_name: "pending-new.pdf".into(),
                file_size: 4,
                file_md5: None,
            },
        )
        .await,
        "upload::init(PENDING_CONFIRMATION)",
    )
    .await;

    let uploading = insert_upload_session(&f, UploadStatus::Uploading, "pending-stage.pdf").await;
    let merging = insert_upload_session(&f, UploadStatus::Merging, "pending-merging.pdf").await;
    let before_upload_stages = snapshot(&f).await;
    assert_rejected_without_mutation(
        &f,
        &before_upload_stages,
        service::upload::put_chunk(&f.state, &f.member, &uploading, 0, b"test").await,
        "upload::put_chunk(PENDING_CONFIRMATION)",
    )
    .await;
    assert_rejected_without_mutation(
        &f,
        &before_upload_stages,
        service::upload::merge(&f.state, &f.member, &uploading).await,
        "upload::merge(UPLOADING, PENDING_CONFIRMATION)",
    )
    .await;
    assert_rejected_without_mutation(
        &f,
        &before_upload_stages,
        service::upload::merge(&f.state, &f.member, &merging).await,
        "upload::merge(MERGING, PENDING_CONFIRMATION)",
    )
    .await;
    // Active uploads cannot normally coexist with PendingConfirmation: submit
    // rejects them. The injected sessions above protect late chunk/merge writes;
    // cancelling temporary resources is tested through a reachable retry below.

    let messages_before = messages::Entity::find()
        .filter(messages::Column::ProjectId.eq(f.project_id))
        .count(&f.state.db)
        .await
        .unwrap();
    service::message::create(
        &f.state.db,
        &f.member,
        f.project_id,
        &service::message::MessageCreate {
            content: "待确认阶段仍可沟通".into(),
        },
        "http://localhost",
    )
    .await
    .unwrap();
    let after_message = snapshot(&f).await;
    assert_eq!(
        after_message.project.status,
        ProjectStatus::PendingConfirmation
    );
    assert_eq!(after_message.status_logs, before.status_logs);
    assert_eq!(
        after_message.messages.len() as u64,
        messages_before + 1,
        "待确认阶段留言应成功写入"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn workflow_cancel_retry_after_submit_preserves_frozen_state_and_audit() {
    let f = Fixture::new().await;
    insert_available_file(&f, "cancel-existing.pdf").await;
    let sid = f.init("cancel-retry.pdf").await;
    service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test")
        .await
        .unwrap();
    service::upload::abort(&f.state, &f.member, &sid)
        .await
        .unwrap();
    service::project::submit(
        &f.state.db,
        "http://localhost",
        &f.admin,
        f.project_id,
        &service::project::SubmitReq {
            confirm_side: "SUPPLIER".into(),
        },
    )
    .await
    .unwrap();
    let before = snapshot(&f).await;
    let audit_count = || {
        audit_logs::Entity::find()
            .filter(audit_logs::Column::TargetType.eq("upload_session"))
            .filter(audit_logs::Column::TargetId.eq(sid.clone()))
            .filter(audit_logs::Column::Action.eq("UPLOAD_ABORT"))
            .count(&f.state.db)
    };
    assert_eq!(audit_count().await.unwrap(), 1);
    // The client lost the first cancellation response and retries after submit.
    service::upload::abort(&f.state, &f.member, &sid)
        .await
        .unwrap();
    assert_eq!(before, snapshot(&f).await);
    assert_eq!(
        audit_count().await.unwrap(),
        1,
        "retry must not add another cancellation audit"
    );
    assert!(!crate::storage::tmp_dir(&f.state.cfg.storage.root, &sid).exists());
}

async fn assert_terminal_writes_frozen(f: &Fixture, status: ProjectStatus, file_id: u64) {
    let before = snapshot(f).await;
    let project = before.project.clone();
    assert_rejected_without_mutation(
        f,
        &before,
        service::project::update(
            &f.state.db,
            &f.admin,
            f.project_id,
            &service::project::ProjectUpsert {
                name: format!("{}-blocked", project.name),
                description: Some("终态资料不应修改".into()),
                supplier_id: project.supplier_id,
            },
        )
        .await,
        "project::update(terminal)",
    )
    .await;
    assert_rejected_without_mutation(
        f,
        &before,
        service::project::set_members(
            &f.state.db,
            &f.admin,
            f.project_id,
            &service::project::MembersSet {
                user_ids: vec![f.member.id],
            },
        )
        .await,
        "project::set_members(terminal)",
    )
    .await;
    assert_rejected_without_mutation(
        f,
        &before,
        service::file::delete(&f.state, &f.admin, file_id).await,
        "file::delete(terminal)",
    )
    .await;
    assert_rejected_without_mutation(
        f,
        &before,
        service::upload::init(
            &f.state,
            &f.member,
            &service::upload::InitReq {
                project_id: f.project_id,
                file_name: "terminal-new.pdf".into(),
                file_size: 4,
                file_md5: None,
            },
        )
        .await,
        "upload::init(terminal)",
    )
    .await;
    assert_rejected_without_mutation(
        f,
        &before,
        service::message::create(
            &f.state.db,
            &f.member,
            f.project_id,
            &service::message::MessageCreate {
                content: "终态不应留言".into(),
            },
            "http://localhost",
        )
        .await,
        "message::create(terminal)",
    )
    .await;
    assert_rejected_without_mutation(
        f,
        &before,
        service::project::delete(&f.state, &f.admin, f.project_id).await,
        "project::delete(terminal)",
    )
    .await;
    let status_request = if status == ProjectStatus::Completed {
        "IN_PROGRESS"
    } else {
        "COMPLETED"
    };
    assert_rejected_without_mutation(
        f,
        &before,
        service::project::set_status(
            &f.state.db,
            &f.admin,
            f.project_id,
            &service::project::StatusChange {
                status: status_request.into(),
            },
        )
        .await,
        "project::set_status(invalid terminal transition)",
    )
    .await;
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn workflow_completed_freezes_all_business_writes() {
    let f = Fixture::new().await;
    let file_id = insert_available_file(&f, "completed-existing.pdf").await;
    let supplier = supplier_user(&f).await;
    service::project::submit(
        &f.state.db,
        "http://localhost",
        &f.admin,
        f.project_id,
        &service::project::SubmitReq {
            confirm_side: "SUPPLIER".into(),
        },
    )
    .await
    .unwrap();
    service::project::confirm(&f.state.db, "http://localhost", &supplier, f.project_id)
        .await
        .unwrap();
    assert_eq!(
        projects::Entity::find_by_id(f.project_id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        ProjectStatus::Completed
    );
    assert_terminal_writes_frozen(&f, ProjectStatus::Completed, file_id).await;
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn workflow_terminated_freezes_writes_and_only_status_permission_restarts() {
    let f = Fixture::new().await;
    let file_id = insert_available_file(&f, "terminated-existing.pdf").await;
    service::project::set_status(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::StatusChange {
            status: "TERMINATED".into(),
        },
    )
    .await
    .unwrap();
    assert_terminal_writes_frozen(&f, ProjectStatus::Terminated, file_id).await;

    let view_all = create_internal_with_permissions(&f, &["project:view_all"], "terminated").await;
    assert!(matches!(
        service::project::set_status(
            &f.state.db,
            &view_all,
            f.project_id,
            &service::project::StatusChange {
                status: "IN_PROGRESS".into(),
            },
        )
        .await,
        Err(AppError::Forbidden)
    ));
    assert_eq!(
        projects::Entity::find_by_id(f.project_id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        ProjectStatus::Terminated
    );
    service::project::set_status(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::StatusChange {
            status: "IN_PROGRESS".into(),
        },
    )
    .await
    .unwrap();
    assert_eq!(
        projects::Entity::find_by_id(f.project_id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        ProjectStatus::InProgress
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn workflow_view_all_does_not_grant_confirm_or_status_but_can_withdraw() {
    let f = Fixture::new().await;
    insert_available_file(&f, "view-all-existing.pdf").await;
    let view_all =
        create_internal_with_permissions(&f, &["project:view_all", "project:withdraw"], "viewall")
            .await;
    assert!(
        service::perm::check_perm(&f.state.db, view_all.id, "project:view_all")
            .await
            .is_ok()
    );
    assert!(matches!(
        service::perm::check_perm(&f.state.db, view_all.id, "project:confirm").await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::perm::check_perm(&f.state.db, view_all.id, "project:status").await,
        Err(AppError::Forbidden)
    ));
    service::project::submit(
        &f.state.db,
        "http://localhost",
        &f.admin,
        f.project_id,
        &service::project::SubmitReq {
            confirm_side: "SUPPLIER".into(),
        },
    )
    .await
    .unwrap();
    assert!(matches!(
        service::project::confirm(&f.state.db, "http://localhost", &view_all, f.project_id).await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::project::set_status(
            &f.state.db,
            &view_all,
            f.project_id,
            &service::project::StatusChange {
                status: "TERMINATED".into(),
            },
        )
        .await,
        Err(AppError::Forbidden)
    ));
    assert_eq!(
        projects::Entity::find_by_id(f.project_id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        ProjectStatus::PendingConfirmation
    );
    service::project::withdraw(&f.state.db, "http://localhost", &view_all, f.project_id)
        .await
        .unwrap();
    let logs = project_status_logs::Entity::find()
        .filter(project_status_logs::Column::ProjectId.eq(f.project_id))
        .order_by_desc(project_status_logs::Column::Id)
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(logs.first().unwrap().action, "WITHDRAW");
    assert_eq!(
        projects::Entity::find_by_id(f.project_id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        ProjectStatus::InProgress
    );
}
