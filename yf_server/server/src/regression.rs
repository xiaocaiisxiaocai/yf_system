//! 必须由 scripts/test-isolated.py 在临时 MySQL 数据库中执行。
use crate::{
    config::Config,
    entity::{enums::*, roles, users},
    middleware::auth::CurrentUser,
    service,
    state::AppState,
};
use migration::MigratorTrait;
use sea_orm::{ColumnTrait, EntityTrait, QueryFilter};

pub struct Fixture {
    pub state: AppState,
    pub admin: CurrentUser,
    pub member: CurrentUser,
    pub project_id: u64,
    pub round_id: u64,
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_disabled_supplier_accounts_remain_manageable_but_cannot_login() {
    use crate::entity::projects;
    let f = Fixture::new().await;
    let sid = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .supplier_id;
    let mut ids = vec![];
    for n in 0..21 {
        let account = service::supplier::create_account(
            &f.state.db,
            &f.admin,
            sid,
            &service::supplier::AccountCreate {
                employee_no: format!("{}s{n}", f.member.employee_no),
                password: "Regression123".into(),
                real_name: format!("账号{n}"),
                email: format!("account{n}@example.invalid"),
            },
        )
        .await
        .unwrap();
        ids.push(account["id"].as_u64().unwrap());
    }
    let listed = service::supplier::list_accounts(&f.state.db, sid)
        .await
        .unwrap();
    assert_eq!(
        listed
            .as_array()
            .unwrap()
            .iter()
            .map(|a| a["id"].as_u64().unwrap())
            .collect::<Vec<_>>(),
        ids
    );
    service::supplier::set_account_status(&f.state.db, &f.admin, ids[0], "DISABLED")
        .await
        .unwrap();
    service::supplier::set_status(&f.state.db, &f.admin, sid, "DISABLED")
        .await
        .unwrap();
    assert!(service::supplier::create_account(
        &f.state.db,
        &f.admin,
        sid,
        &service::supplier::AccountCreate {
            employee_no: format!("{}new", f.member.employee_no),
            password: "Regression123".into(),
            real_name: "禁止新增".into(),
            email: "new@example.invalid".into(),
        }
    )
    .await
    .is_err());
    service::supplier::update_account(
        &f.state.db,
        &f.admin,
        ids[1],
        &service::supplier::AccountUpdate {
            real_name: Some("禁用组织仍可维护资料".into()),
            email: None,
        },
    )
    .await
    .unwrap();
    service::supplier::set_account_status(&f.state.db, &f.admin, ids[1], "ACTIVE")
        .await
        .unwrap();
    let req = crate::dto::LoginRequest {
        employee_no: format!("{}s1", f.member.employee_no),
        password: "Regression123".into(),
        captcha_id: None,
        captcha_code: None,
    };
    assert!(matches!(
        service::auth::login(&f.state.db, &f.state.cfg, &req, None, &f.state.captchas).await,
        Err(crate::error::AppError::Unauthorized(_))
    ));
    service::supplier::set_status(&f.state.db, &f.admin, sid, "ACTIVE")
        .await
        .unwrap();
    service::auth::login(&f.state.db, &f.state.cfg, &req, None, &f.state.captchas)
        .await
        .unwrap();
    assert_eq!(
        users::Entity::find_by_id(ids[0])
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        CommonStatus::Disabled
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_builtin_role_policies_are_enforced_by_services() {
    use crate::entity::role_permissions;
    let f = Fixture::new().await;
    for name in ["系统管理员", "供应商人员", "项目管理员", "内部成员"] {
        let role = roles::Entity::find()
            .filter(roles::Column::Name.eq(name))
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap();
        let before = role_permissions::Entity::find()
            .filter(role_permissions::Column::RoleId.eq(role.id))
            .all(&f.state.db)
            .await
            .unwrap();
        let req = service::role::PermAssign {
            permission_ids: before.iter().map(|p| p.permission_id).collect(),
        };
        let result = service::role::assign_permissions(&f.state.db, &f.admin, role.id, &req).await;
        if matches!(name, "系统管理员" | "供应商人员") {
            assert!(result.is_err());
            assert!(
                service::role::set_status(&f.state.db, &f.admin, role.id, "DISABLED")
                    .await
                    .is_err()
            );
        } else {
            result.unwrap();
        }
        let after = role_permissions::Entity::find()
            .filter(role_permissions::Column::RoleId.eq(role.id))
            .all(&f.state.db)
            .await
            .unwrap();
        assert_eq!(before, after);
        assert_eq!(
            roles::Entity::find_by_id(role.id)
                .one(&f.state.db)
                .await
                .unwrap()
                .unwrap()
                .status,
            CommonStatus::Active
        );
    }
}

async fn member_validity_barrier(action: &str) {
    use crate::entity::{audit_logs, permissions, project_members, projects, role_permissions};
    use sea_orm::{ConnectionTrait, DbBackend, QuerySelect, Statement, TransactionTrait};
    let f = Fixture::new().await;
    let perm = permissions::Entity::find()
        .filter(permissions::Column::Code.eq("project:member"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .id;
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: "成员并发角色".into(),
            description: None,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    service::role::assign_permissions(
        &f.state.db,
        &f.admin,
        role,
        &service::role::PermAssign {
            permission_ids: vec![perm],
        },
    )
    .await
    .unwrap();
    service::user::assign_roles(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::RoleAssign {
            role_ids: vec![role],
        },
    )
    .await
    .unwrap();
    let before = project_members::Entity::find()
        .filter(project_members::Column::ProjectId.eq(f.project_id))
        .all(&f.state.db)
        .await
        .unwrap();
    let before_audit = audit_logs::Entity::find()
        .filter(audit_logs::Column::TargetId.eq(f.project_id.to_string()))
        .filter(audit_logs::Column::Action.eq("PROJECT_MEMBERS"))
        .all(&f.state.db)
        .await
        .unwrap()
        .len();
    let txn = f.state.db.begin().await.unwrap();
    service::perm::lock_management_state(&txn).await.unwrap();
    projects::Entity::find_by_id(f.project_id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let (db, pid, uid, actor) = (
        f.state.db.clone(),
        f.project_id,
        f.member.id,
        if action == "target" {
            f.admin.clone()
        } else {
            f.member.clone()
        },
    );
    let pending = tokio::spawn(async move {
        service::project::set_members(
            &db,
            &actor,
            pid,
            &service::project::MembersSet {
                user_ids: vec![uid],
            },
        )
        .await
    });
    await_owned_lock(&f.state.db, blocker).await;
    if action == "permission" {
        role_permissions::Entity::delete_many()
            .filter(role_permissions::Column::RoleId.eq(role))
            .exec(&txn)
            .await
            .unwrap();
    } else {
        txn.execute(Statement::from_string(
            DbBackend::MySql,
            format!(
                "UPDATE users SET status='DISABLED' WHERE id={}",
                f.member.id
            ),
        ))
        .await
        .unwrap();
    }
    txn.commit().await.unwrap();
    let result = pending.await.unwrap();
    assert!(
        result.is_err(),
        "{action} invalidated during lock wait but membership committed"
    );
    let after = project_members::Entity::find()
        .filter(project_members::Column::ProjectId.eq(f.project_id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(after, before);
    let after_audit = audit_logs::Entity::find()
        .filter(audit_logs::Column::TargetId.eq(f.project_id.to_string()))
        .filter(audit_logs::Column::Action.eq("PROJECT_MEMBERS"))
        .all(&f.state.db)
        .await
        .unwrap()
        .len();
    assert_eq!(before_audit, after_audit);
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_members_recheck_disabled_target_after_wait() {
    member_validity_barrier("target").await;
}
#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_members_recheck_disabled_operator_after_wait() {
    member_validity_barrier("operator").await;
}
#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_members_recheck_revoked_permission_after_wait() {
    member_validity_barrier("permission").await;
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_member_sets_deduplicate_ids_and_preserve_operator() {
    use crate::entity::project_members;
    let f = Fixture::new().await;
    service::project::set_members(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::MembersSet {
            user_ids: vec![f.member.id, f.member.id, f.admin.id, f.admin.id],
        },
    )
    .await
    .unwrap();
    let rows = project_members::Entity::find()
        .filter(project_members::Column::ProjectId.eq(f.project_id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(rows.len(), 2);
    service::project::set_members(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::MembersSet { user_ids: vec![] },
    )
    .await
    .unwrap();
    let rows = project_members::Entity::find()
        .filter(project_members::Column::ProjectId.eq(f.project_id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].user_id, f.admin.id);
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_configuration_applies_to_new_uploads_and_preserves_existing_sessions() {
    use crate::entity::system_configs;
    use md5::Digest;
    use service::config::{ConfigBatch, ConfigUpdate};
    let f = Fixture::new().await;
    let original = system_configs::Entity::find()
        .all(&f.state.db)
        .await
        .unwrap();
    let update = |values: Vec<(&str, String)>| ConfigBatch {
        items: values
            .into_iter()
            .map(|(key, value)| ConfigUpdate {
                key: key.into(),
                value,
            })
            .collect(),
    };
    let request = service::upload::InitReq {
        project_id: f.project_id,
        round_id: f.round_id,
        file_name: "config.pdf".into(),
        file_size: 1024 * 1024,
        file_md5: Some(format!("{:x}", md5::Md5::digest(b"config fixture"))),
    };
    service::config::update(
        &f.state.db,
        &f.admin,
        &update(vec![
            ("upload.chunk_size", (256 * 1024).to_string()),
            ("upload.max_file_size", (1024 * 1024).to_string()),
            ("upload.allowed_exts", "pdf".into()),
        ]),
    )
    .await
    .unwrap();
    let old = service::upload::init(&f.state, &f.member, &request)
        .await
        .unwrap();
    service::config::update(
        &f.state.db,
        &f.admin,
        &update(vec![("upload.chunk_size", (512 * 1024).to_string())]),
    )
    .await
    .unwrap();
    let resumed = service::upload::init(&f.state, &f.member, &request)
        .await
        .unwrap();
    let fresh = service::upload::init(
        &f.state,
        &f.member,
        &service::upload::InitReq {
            file_name: "new-config.pdf".into(),
            file_md5: None,
            ..request
        },
    )
    .await
    .unwrap();
    let too_big = service::upload::init(
        &f.state,
        &f.member,
        &service::upload::InitReq {
            project_id: f.project_id,
            round_id: f.round_id,
            file_name: "over.pdf".into(),
            file_size: 1024 * 1024 + 1,
            file_md5: None,
        },
    )
    .await;
    let invalid_ext = service::upload::init(
        &f.state,
        &f.member,
        &service::upload::InitReq {
            project_id: f.project_id,
            round_id: f.round_id,
            file_name: "blocked.xlsx".into(),
            file_size: 4,
            file_md5: None,
        },
    )
    .await;
    let invalid_batch = service::config::update(
        &f.state.db,
        &f.admin,
        &update(vec![
            ("upload.chunk_size", (1024 * 1024).to_string()),
            ("unknown.fixture.key", "x".into()),
        ]),
    )
    .await;
    let chunk = system_configs::Entity::find_by_id("upload.chunk_size")
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .cfg_value;
    service::config::update(
        &f.state.db,
        &f.admin,
        &ConfigBatch {
            items: original
                .into_iter()
                .map(|c| ConfigUpdate {
                    key: c.cfg_key,
                    value: c.cfg_value.unwrap_or_default(),
                })
                .collect(),
        },
    )
    .await
    .unwrap();
    assert_eq!(old["chunkSize"], 256 * 1024);
    assert_eq!(old["sessionId"], resumed["sessionId"]);
    assert_eq!(resumed["chunkSize"], 256 * 1024);
    assert_eq!(fresh["chunkSize"], 512 * 1024);
    assert_eq!(fresh["totalChunks"], 2);
    assert!(too_big.is_err() && invalid_ext.is_err() && invalid_batch.is_err());
    assert_eq!(
        chunk,
        Some((512 * 1024).to_string()),
        "未知参数使整个批次回滚"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_message_receipts_follow_membership_and_ignore_view_all_readers() {
    use sea_orm::{ActiveModelTrait, Set};
    let f = Fixture::new().await;
    // The administrator in this test is a view_all outsider, not the creator.
    let mut project: crate::entity::projects::ActiveModel =
        crate::entity::projects::Entity::find_by_id(f.project_id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .into();
    project.created_by = Set(f.member.id);
    project.update(&f.state.db).await.unwrap();
    let manager = roles::Entity::find()
        .filter(roles::Column::Name.eq("项目管理员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    service::user::assign_roles(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::RoleAssign {
            role_ids: vec![manager.id],
        },
    )
    .await
    .unwrap();
    service::project::set_members(
        &f.state.db,
        &f.member,
        f.project_id,
        &service::project::MembersSet {
            user_ids: vec![f.member.id],
        },
    )
    .await
    .unwrap();
    let msg = service::message::create(
        &f.state.db,
        &f.member,
        f.project_id,
        &service::message::MessageCreate {
            content: "dynamic receipt".into(),
            round_id: None,
        },
        "http://localhost",
    )
    .await
    .unwrap();
    let id = msg["id"].as_u64().unwrap();
    service::message::mark_read(
        &f.state.db,
        &f.admin,
        &service::message::MarkRead { ids: vec![id; 500] },
    )
    .await
    .unwrap();
    assert!(service::message::mark_read(
        &f.state.db,
        &f.admin,
        &service::message::MarkRead { ids: vec![id; 501] }
    )
    .await
    .is_err());
    let outsiders = service::message::reads(&f.state.db, &f.admin, id)
        .await
        .unwrap();
    assert!(outsiders["readers"].as_array().unwrap().is_empty());
    assert!(outsiders["unread"].as_array().unwrap().is_empty());
    service::project::set_members(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::MembersSet {
            user_ids: vec![f.member.id, f.admin.id],
        },
    )
    .await
    .unwrap();
    let added = service::message::reads(&f.state.db, &f.admin, id)
        .await
        .unwrap();
    assert_eq!(added["readers"].as_array().unwrap().len(), 1);
    assert_eq!(added["readers"][0]["userId"], f.admin.id);
    service::project::set_members(
        &f.state.db,
        &f.member,
        f.project_id,
        &service::project::MembersSet {
            user_ids: vec![f.member.id],
        },
    )
    .await
    .unwrap();
    let removed = service::message::reads(&f.state.db, &f.admin, id)
        .await
        .unwrap();
    assert!(removed["readers"].as_array().unwrap().is_empty());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn review_participants_include_creator_and_exclude_disabled_accounts() {
    use crate::entity::{email_outbox, project_members, projects};
    use sea_orm::{ActiveModelTrait, Set};
    let f = Fixture::new().await;
    let mut project: projects::ActiveModel = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .into();
    project.created_by = Set(f.member.id);
    project.update(&f.state.db).await.unwrap();
    project_members::Entity::delete_by_id((f.project_id, f.member.id))
        .exec(&f.state.db)
        .await
        .unwrap();
    let msg = service::message::create(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::message::MessageCreate {
            content: "creator is still a participant".into(),
            round_id: None,
        },
        "http://localhost",
    )
    .await
    .unwrap();
    assert_eq!(
        msg["totalCount"], 1,
        "active creator counts even without member binding"
    );
    let queued = email_outbox::Entity::find()
        .filter(email_outbox::Column::ProjectId.eq(f.project_id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert!(
        queued
            .iter()
            .any(|row| row.recipient_user_id == Some(f.member.id)),
        "creator receives the notification"
    );
    service::message::mark_read(
        &f.state.db,
        &f.member,
        &service::message::MarkRead {
            ids: vec![msg["id"].as_u64().unwrap()],
        },
    )
    .await
    .unwrap();
    let receipt = service::message::reads(&f.state.db, &f.admin, msg["id"].as_u64().unwrap())
        .await
        .unwrap();
    assert_eq!(receipt["readers"][0]["userId"], f.member.id);
    service::project::set_members(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::MembersSet {
            user_ids: vec![f.member.id],
        },
    )
    .await
    .unwrap();
    service::user::set_status(&f.state.db, &f.admin, f.member.id, "DISABLED")
        .await
        .unwrap();
    let receipt = service::message::reads(&f.state.db, &f.admin, msg["id"].as_u64().unwrap())
        .await
        .unwrap();
    assert!(
        receipt["readers"].as_array().unwrap().is_empty(),
        "disabled creator/member is no longer a reader"
    );
    assert!(receipt["unread"].as_array().unwrap().is_empty());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_message_mark_read_mixed_ids_only_records_eligible_messages() {
    use crate::entity::{message_reads, projects};
    let f = Fixture::new().await;
    let eligible = service::message::create(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::message::MessageCreate {
            content: "eligible read".into(),
            round_id: None,
        },
        "",
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    let self_message = service::message::create(
        &f.state.db,
        &f.member,
        f.project_id,
        &service::message::MessageCreate {
            content: "self message ignored".into(),
            round_id: None,
        },
        "",
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    let supplier = service::supplier::create(
        &f.state.db,
        &f.admin,
        &service::supplier::SupplierUpsert {
            name: "外部回执供应商".into(),
            remark: None,
        },
    )
    .await
    .unwrap();
    let foreign = service::project::create(
        &f.state.db,
        &f.admin,
        &service::project::ProjectUpsert {
            name: "外部回执项目".into(),
            description: None,
            supplier_id: supplier["id"].as_u64().unwrap(),
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    service::project::set_status(
        &f.state.db,
        &f.admin,
        foreign,
        &service::project::StatusChange {
            status: "IN_PROGRESS".into(),
        },
    )
    .await
    .unwrap();
    let foreign_message = service::message::create(
        &f.state.db,
        &f.admin,
        foreign,
        &service::message::MessageCreate {
            content: "foreign ignored".into(),
            round_id: None,
        },
        "",
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    assert_ne!(
        projects::Entity::find_by_id(foreign)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .id,
        f.project_id
    );

    service::message::mark_read(
        &f.state.db,
        &f.member,
        &service::message::MarkRead {
            ids: vec![eligible, foreign_message, u64::MAX, self_message, eligible],
        },
    )
    .await
    .unwrap();

    let rows = message_reads::Entity::find()
        .filter(message_reads::Column::UserId.eq(f.member.id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].message_id, eligible);
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_round_three_way_decision_commits_exactly_once() {
    use crate::entity::{audit_logs, email_outbox, round_status_logs, rounds};
    let f = Fixture::new().await;
    let reason = service::round::RejectReq {
        reason: Some("尺寸不符".into()),
    };
    let (a, b, c) = tokio::join!(
        service::round::confirm(&f.state.db, &f.state.cfg, &f.admin, f.round_id),
        service::round::reject(&f.state.db, &f.state.cfg, &f.admin, f.round_id, &reason),
        service::round::cancel(&f.state.db, &f.state.cfg, &f.admin, f.round_id)
    );
    let results = [a, b, c];
    assert_eq!(results.iter().filter(|r| r.is_ok()).count(), 1);
    assert!(results
        .iter()
        .filter_map(|r| r.as_ref().err())
        .all(|e| matches!(e, crate::error::AppError::Conflict(_))));
    let round = rounds::Entity::find_by_id(f.round_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_ne!(round.status, RoundStatus::Pending);
    let logs = round_status_logs::Entity::find()
        .filter(round_status_logs::Column::RoundId.eq(f.round_id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(logs.len(), 2, "创建和唯一决定各一条历史");
    let audits = audit_logs::Entity::find()
        .filter(audit_logs::Column::TargetId.eq(f.round_id.to_string()))
        .filter(audit_logs::Column::Action.is_in(["ROUND_CONFIRM", "ROUND_REJECT", "ROUND_CANCEL"]))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(audits.len(), 1);
    let notices = email_outbox::Entity::find()
        .filter(email_outbox::Column::RoundId.eq(f.round_id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(notices.len(), 1, "唯一决定只给唯一其他参与者入队一封通知");
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_user_invalid_binding_rolls_back_profile_and_audit() {
    use crate::entity::{audit_logs, user_roles};
    let f = Fixture::new().await;
    let before = service::user::to_json(
        &f.state.db,
        &users::Entity::find_by_id(f.member.id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap(),
    )
    .await;
    let role = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.eq(f.member.id))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .role_id;
    for invalid_department in [true, false] {
        let req = service::user::UserUpdate {
            real_name: Some("不应被写入".into()),
            email: Some("changed@example.invalid".into()),
            department_id: if invalid_department {
                Some(Some(u64::MAX))
            } else {
                Some(None)
            },
            role_id: Some(if invalid_department { role } else { u64::MAX }),
            role_ids: None,
        };
        assert!(
            service::user::update(&f.state.db, &f.admin, f.member.id, &req)
                .await
                .is_err()
        );
        let after = service::user::to_json(
            &f.state.db,
            &users::Entity::find_by_id(f.member.id)
                .one(&f.state.db)
                .await
                .unwrap()
                .unwrap(),
        )
        .await;
        assert_eq!(after, before);
    }
    assert!(audit_logs::Entity::find()
        .filter(audit_logs::Column::TargetId.eq(f.member.id.to_string()))
        .filter(audit_logs::Column::Action.eq("USER_UPDATE"))
        .all(&f.state.db)
        .await
        .unwrap()
        .is_empty());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_upload_protocol_errors_recover_without_partial_files() {
    use crate::entity::files;
    use crate::error::AppError;
    let f = Fixture::new().await;
    assert!(matches!(
        service::upload::status(&f.state, &f.member, "missing-session").await,
        Err(AppError::NotFound)
    ));
    let sid = f.init("protocol.pdf").await;
    for (index, bytes) in [
        (1, b"test".as_slice()),
        (u32::MAX, b"test".as_slice()),
        (0, b"bad".as_slice()),
        (0, b"extra".as_slice()),
    ] {
        assert!(matches!(
            service::upload::put_chunk(&f.state, &f.member, &sid, index, bytes).await,
            Err(AppError::BadRequest(_))
        ));
    }
    assert!(matches!(
        service::upload::merge(&f.state, &f.member, &sid).await,
        Err(AppError::BadRequest(_))
    ));
    assert!(files::Entity::find()
        .filter(files::Column::ProjectId.eq(f.project_id))
        .all(&f.state.db)
        .await
        .unwrap()
        .is_empty());
    assert!(matches!(
        service::upload::put_chunk(&f.state, &f.admin, &sid, 0, b"test").await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::upload::status(&f.state, &f.admin, &sid).await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::upload::abort(&f.state, &f.admin, &sid).await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::upload::merge(&f.state, &f.admin, &sid).await,
        Err(AppError::Forbidden)
    ));
    service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test")
        .await
        .unwrap();
    assert!(matches!(
        service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"bad").await,
        Err(AppError::BadRequest(_))
    ));
    assert_eq!(
        tokio::fs::read(crate::storage::chunk_path(
            &f.state.cfg.storage.root,
            &sid,
            0
        ))
        .await
        .unwrap(),
        b"test"
    );
    service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test")
        .await
        .unwrap();
    let result = service::upload::merge(&f.state, &f.member, &sid)
        .await
        .unwrap();
    assert_eq!(
        service::upload::merge(&f.state, &f.member, &sid)
            .await
            .unwrap(),
        result
    );
    let rows = files::Entity::find()
        .filter(files::Column::ProjectId.eq(f.project_id))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(rows.len(), 1);
    assert!(matches!(
        service::upload::abort(&f.state, &f.member, &sid).await,
        Err(AppError::Conflict(_))
    ));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn notification_recipients_exclude_disabled_users_and_dedupe_emails() {
    use crate::entity::{email_outbox, projects};
    use sea_orm::{ActiveModelTrait, Set};
    let f = Fixture::new().await;
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
            employee_no: format!("{}s", f.member.employee_no),
            password: "Regression123".into(),
            real_name: "duplicate recipient".into(),
            email: "member@example.invalid".into(),
        },
    )
    .await
    .unwrap();
    let account_id = account["id"].as_u64().unwrap();
    let member = users::Entity::find_by_id(f.member.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let mut member: users::ActiveModel = member.into();
    for (index, expected) in [(0, 1), (1, 2), (2, 2), (3, 2)] {
        if index == 1 {
            member.status = Set(CommonStatus::Disabled);
            member.clone().update(&f.state.db).await.unwrap();
        }
        if index == 2 {
            service::supplier::set_account_status(&f.state.db, &f.admin, account_id, "DISABLED")
                .await
                .unwrap();
        }
        if index == 3 {
            service::supplier::set_account_status(&f.state.db, &f.admin, account_id, "ACTIVE")
                .await
                .unwrap();
            service::config::update(
                &f.state.db,
                &f.admin,
                &service::config::ConfigBatch {
                    items: vec![service::config::ConfigUpdate {
                        key: "notify.enabled".into(),
                        value: "false".into(),
                    }],
                },
            )
            .await
            .unwrap();
        }
        service::notify::enqueue_file_notice(
            &f.state.db,
            &project,
            1,
            "fixture.pdf",
            &f.admin,
            "http://localhost",
        )
        .await
        .unwrap();
        let rows = email_outbox::Entity::find()
            .filter(email_outbox::Column::ProjectId.eq(f.project_id))
            .all(&f.state.db)
            .await
            .unwrap();
        assert_eq!(rows.len(), expected, "recipient stage {index}");
        if index == 1 {
            assert_eq!(rows.last().unwrap().recipient_user_id, Some(account_id));
        }
    }
    service::config::update(
        &f.state.db,
        &f.admin,
        &service::config::ConfigBatch {
            items: vec![service::config::ConfigUpdate {
                key: "notify.enabled".into(),
                value: "true".into(),
            }],
        },
    )
    .await
    .unwrap();
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn deleted_message_receipts_are_not_accessible() {
    let f = Fixture::new().await;
    let created = service::message::create(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::message::MessageCreate {
            content: "receipt deletion regression".into(),
            round_id: None,
        },
        "http://localhost",
    )
    .await
    .unwrap();
    let id = created["id"].as_u64().unwrap();
    assert!(service::message::reads(&f.state.db, &f.admin, id)
        .await
        .is_ok());
    service::message::delete(&f.state.db, &f.admin, id)
        .await
        .unwrap();
    assert!(matches!(
        service::message::reads(&f.state.db, &f.admin, id).await,
        Err(crate::error::AppError::NotFound)
    ));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_role_description_boundaries_preserve_existing_data() {
    let f = Fixture::new().await;
    let mut req = service::role::RoleUpsert {
        name: "角色说明边界".into(),
        description: Some("文".repeat(256)),
    };
    assert!(matches!(
        service::role::create(&f.state.db, &f.admin, &req).await,
        Err(crate::error::AppError::BadRequest(_))
    ));
    req.description = Some("文".repeat(255));
    let created = service::role::create(&f.state.db, &f.admin, &req)
        .await
        .unwrap();
    let id = created["id"].as_u64().unwrap();
    req.description = Some("文".repeat(256));
    assert!(matches!(
        service::role::update(&f.state.db, &f.admin, id, &req).await,
        Err(crate::error::AppError::BadRequest(_))
    ));
    assert_eq!(
        roles::Entity::find_by_id(id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .description
            .unwrap()
            .chars()
            .count(),
        255
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_auth_failure_threshold_lock_expiry_and_reset() {
    use crate::{dto::LoginRequest, error::AppError};
    use sea_orm::{ActiveModelTrait, Set};
    let f = Fixture::new().await;
    let mut req = LoginRequest {
        employee_no: format!(" {} ", f.member.employee_no),
        password: "Wrong123".into(),
        captcha_id: None,
        captcha_code: None,
    };
    for _ in 0..2 {
        assert!(matches!(
            service::auth::login(&f.state.db, &f.state.cfg, &req, None, &f.state.captchas).await,
            Err(AppError::Unauthorized(_))
        ));
    }
    assert!(matches!(
        service::auth::login(&f.state.db, &f.state.cfg, &req, None, &f.state.captchas).await,
        Err(AppError::CaptchaRequired)
    ));
    let row = users::Entity::find_by_id(f.member.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(row.failed_login_attempts, 3);
    for fourth in [true, false] {
        let (id, _) = service::captcha::issue(&f.state.captchas);
        req.captcha_code = Some(f.state.captchas.lock().unwrap()[&id].0.to_lowercase());
        req.captcha_id = Some(id);
        let result =
            service::auth::login(&f.state.db, &f.state.cfg, &req, None, &f.state.captchas).await;
        if fourth {
            assert!(matches!(result, Err(AppError::CaptchaRequired)));
        } else {
            assert!(matches!(result, Err(AppError::Locked)));
        }
    }
    req.password = "Regression123".into();
    req.captcha_id = None;
    req.captcha_code = None;
    assert!(matches!(
        service::auth::login(&f.state.db, &f.state.cfg, &req, None, &f.state.captchas).await,
        Err(AppError::Locked)
    ));
    let row = users::Entity::find_by_id(f.member.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert!(row.locked_until.unwrap() > chrono::Utc::now() + chrono::Duration::minutes(29));
    let mut update: users::ActiveModel = row.into();
    update.locked_until = Set(Some(chrono::Utc::now() - chrono::Duration::seconds(1)));
    update.update(&f.state.db).await.unwrap();
    assert!(
        service::auth::login(&f.state.db, &f.state.cfg, &req, None, &f.state.captchas)
            .await
            .is_ok()
    );
    let row = users::Entity::find_by_id(f.member.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(row.failed_login_attempts, 0);
    assert!(row.locked_until.is_none());
    req.password = "Wrong123".into();
    assert!(matches!(
        service::auth::login(&f.state.db, &f.state.cfg, &req, None, &f.state.captchas).await,
        Err(AppError::Unauthorized(_))
    ));
    assert_eq!(
        users::Entity::find_by_id(f.member.id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .failed_login_attempts,
        1
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_deleted_file_gc_retains_recent_deletion_and_reclaims_expired() {
    use crate::entity::files;
    use sea_orm::{ActiveModelTrait, Set};
    let f = Fixture::new().await;
    for (name, age_days, keep) in [("recent.pdf", 29, true), ("expired.pdf", 31, false)] {
        let sid = f.init(name).await;
        service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test")
            .await
            .unwrap();
        let file = service::upload::merge(&f.state, &f.member, &sid)
            .await
            .unwrap();
        let id = file["id"].as_u64().unwrap();
        let row = files::Entity::find_by_id(id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap();
        let path = std::path::Path::new(&f.state.cfg.storage.root).join(&row.storage_path);
        let mut update: files::ActiveModel = row.into();
        update.status = Set(FileStatus::Deleted);
        update.created_at = Set(chrono::Utc::now() - chrono::Duration::days(90));
        update.deleted_at = Set(Some(chrono::Utc::now() - chrono::Duration::days(age_days)));
        update.update(&f.state.db).await.unwrap();
        service::gc::run_all(&f.state).await;
        assert_eq!(
            path.exists(),
            keep,
            "physical retention must start at deletion time"
        );
        assert_eq!(
            files::Entity::find_by_id(id)
                .one(&f.state.db)
                .await
                .unwrap()
                .is_some(),
            keep
        );
    }
}

impl Fixture {
    pub async fn new() -> Self {
        let url = std::env::var("YF_TEST_DATABASE_URL").expect("请使用隔离测试脚本");
        assert!(url.rsplit('/').next().unwrap().starts_with("yf_test_"));
        let db = sea_orm::Database::connect(&url).await.unwrap();
        migration::Migrator::up(&db, None).await.unwrap();
        let mut cfg: Config = toml::from_str(include_str!("../../config.toml")).unwrap();
        cfg.database.url = url;
        cfg.jwt.secret = "isolated-regression-test-secret-32-characters".into();
        cfg.storage.root = std::path::Path::new(&std::env::var("YF_TEST_STORAGE_ROOT").unwrap())
            .join(uuid::Uuid::new_v4().to_string())
            .to_string_lossy()
            .into_owned();
        let state = AppState::new(db, cfg);
        let admin_row = users::Entity::find()
            .filter(users::Column::EmployeeNo.eq("admin"))
            .one(&state.db)
            .await
            .unwrap()
            .unwrap();
        let admin = CurrentUser {
            id: admin_row.id,
            employee_no: admin_row.employee_no,
            user_type: UserType::Internal,
            supplier_id: None,
        };
        let role = roles::Entity::find()
            .filter(roles::Column::Name.eq("内部成员"))
            .one(&state.db)
            .await
            .unwrap()
            .unwrap();
        let employee_no = format!("t{}", &uuid::Uuid::new_v4().simple().to_string()[..24]);
        let member_row = service::user::create(
            &state.db,
            &admin,
            &service::user::UserCreate {
                employee_no: employee_no.clone(),
                password: "Regression123".into(),
                real_name: "测试成员".into(),
                email: "member@example.invalid".into(),
                department_id: None,
                role_id: Some(role.id),
                role_ids: None,
            },
        )
        .await
        .unwrap();
        let member = CurrentUser {
            id: member_row["id"].as_u64().unwrap(),
            employee_no: employee_no.clone(),
            user_type: UserType::Internal,
            supplier_id: None,
        };
        let supplier = service::supplier::create(
            &state.db,
            &admin,
            &service::supplier::SupplierUpsert {
                name: "回归供应商".into(),
                remark: None,
            },
        )
        .await
        .unwrap();
        let p = service::project::create(
            &state.db,
            &admin,
            &service::project::ProjectUpsert {
                name: "回归项目".into(),
                description: None,
                supplier_id: supplier["id"].as_u64().unwrap(),
            },
        )
        .await
        .unwrap();
        let project_id = p["id"].as_u64().unwrap();
        service::project::set_members(
            &state.db,
            &admin,
            project_id,
            &service::project::MembersSet {
                user_ids: vec![member.id],
            },
        )
        .await
        .unwrap();
        service::project::set_status(
            &state.db,
            &admin,
            project_id,
            &service::project::StatusChange {
                status: "IN_PROGRESS".into(),
            },
        )
        .await
        .unwrap();
        let r = service::round::create(
            &state.db,
            &admin,
            project_id,
            &service::round::RoundCreate {
                title: Some("回归轮次".into()),
                remark: None,
                confirm_side: "COMPANY".into(),
            },
        )
        .await
        .unwrap();
        Self {
            state,
            admin,
            member,
            project_id,
            round_id: r["id"].as_u64().unwrap(),
        }
    }

    pub async fn init(&self, name: &str) -> String {
        service::upload::init(
            &self.state,
            &self.member,
            &service::upload::InitReq {
                project_id: self.project_id,
                round_id: self.round_id,
                file_name: name.into(),
                file_size: 4,
                file_md5: None,
            },
        )
        .await
        .unwrap()["sessionId"]
            .as_str()
            .unwrap()
            .into()
    }
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn upload_resumes_with_matching_content_md5() {
    let f = Fixture::new().await;
    let input = service::upload::InitReq {
        project_id: f.project_id,
        round_id: f.round_id,
        file_name: "resume.pdf".into(),
        file_size: 4,
        file_md5: Some("098f6bcd4621d373cade4e832627b4f6".into()),
    };
    let first = service::upload::init(&f.state, &f.member, &input)
        .await
        .unwrap();
    let sid = first["sessionId"].as_str().unwrap();
    service::upload::put_chunk(&f.state, &f.member, sid, 0, b"test")
        .await
        .unwrap();
    assert_eq!(
        sid,
        service::upload::init(&f.state, &f.member, &input)
            .await
            .unwrap()["sessionId"],
        "相同内容摘要应找到原会话"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn uploads_without_md5_do_not_reuse_same_named_content() {
    let f = Fixture::new().await;
    let first = f.init("replaced.pdf").await;
    service::upload::put_chunk(&f.state, &f.member, &first, 0, b"old!")
        .await
        .unwrap();
    let second = f.init("replaced.pdf").await;
    assert_ne!(first, second, "无摘要无法证明内容相同，不得自动跳过旧分片");
    service::upload::put_chunk(&f.state, &f.member, &second, 0, b"new!")
        .await
        .unwrap();
    let file = service::upload::merge(&f.state, &f.member, &second)
        .await
        .unwrap();
    let row = crate::entity::files::Entity::find_by_id(file["id"].as_u64().unwrap())
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let stored = std::path::Path::new(&f.state.cfg.storage.root).join(row.storage_path);
    assert_eq!(tokio::fs::read(stored).await.unwrap(), b"new!");
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn removed_member_cannot_upload_or_merge_existing_session() {
    let f = Fixture::new().await;
    let sid = f.init("access.pdf").await;
    service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test")
        .await
        .unwrap();
    service::project::set_members(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::MembersSet { user_ids: vec![] },
    )
    .await
    .unwrap();
    assert!(
        matches!(
            service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test").await,
            Err(crate::error::AppError::OutOfScope)
        ),
        "移出成员后分片必须拒绝"
    );
    assert!(
        matches!(
            service::upload::merge(&f.state, &f.member, &sid).await,
            Err(crate::error::AppError::OutOfScope)
        ),
        "移出成员后合并必须拒绝"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn batch_download_preserves_same_named_files() {
    let f = Fixture::new().await;
    let mut ids = vec![];
    for (name, contents) in [
        ("drawing.pdf", b"one!"),
        ("drawing.pdf", b"two!"),
        ("drawing (2).pdf", b"tri!"),
        ("DRAWING.PDF", b"four"),
    ] {
        let sid = f.init(name).await;
        service::upload::put_chunk(&f.state, &f.member, &sid, 0, contents)
            .await
            .unwrap();
        let file = service::upload::merge(&f.state, &f.member, &sid)
            .await
            .unwrap();
        ids.push(file["id"].as_u64().unwrap());
    }
    let response = service::file::batch_download(
        &f.state,
        &f.member,
        &service::file::BatchDownloadReq { ids },
    )
    .await
    .expect("同名文件应可打包");
    let bytes = axum::body::to_bytes(response.into_body(), 1024 * 1024)
        .await
        .unwrap();
    let mut archive = zip::ZipArchive::new(std::io::Cursor::new(bytes)).unwrap();
    let mut contents = Vec::new();
    let mut names = std::collections::HashSet::new();
    for i in 0..archive.len() {
        let mut file = archive.by_index(i).unwrap();
        names.insert(file.name().to_lowercase());
        let mut body = String::new();
        std::io::Read::read_to_string(&mut file, &mut body).unwrap();
        contents.push(body);
    }
    contents.sort();
    assert_eq!(contents, vec!["four", "one!", "tri!", "two!"]);
    assert_eq!(names.len(), 4);
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn project_creation_and_update_do_not_require_legacy_code() {
    let f = Fixture::new().await;
    let project = service::project::detail(&f.state.db, &f.admin, f.project_id)
        .await
        .unwrap();
    assert!(project.get("code").is_none());
    let input = service::project::ProjectUpsert {
        name: "无编码项目".into(),
        description: None,
        supplier_id: project["supplierId"].as_u64().unwrap(),
    };
    let created = service::project::create(&f.state.db, &f.admin, &input)
        .await
        .unwrap();
    let updated = service::project::update(
        &f.state.db,
        &f.admin,
        created["id"].as_u64().unwrap(),
        &service::project::ProjectUpsert {
            name: "修改名称".into(),
            description: None,
            supplier_id: project["supplierId"].as_u64().unwrap(),
        },
    )
    .await
    .expect("项目不应要求历史编码");
    assert_eq!(updated["name"], "修改名称");
    assert!(updated.get("code").is_none());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_validation_rejects_missing_department_parent() {
    let f = Fixture::new().await;
    let mut req = service::dept::DeptUpsert {
        name: "有效部门".into(),
        parent_id: None,
        sort_no: Some(0),
    };
    let d = service::dept::create(&f.state.db, &f.admin, &req)
        .await
        .unwrap();
    req.parent_id = Some(u64::MAX - 1);
    let result =
        service::dept::update(&f.state.db, &f.admin, d["id"].as_u64().unwrap(), &req).await;
    assert!(
        matches!(result, Err(crate::error::AppError::BadRequest(_))),
        "invalid parent must be a validation error: {result:?}"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn role_creation_does_not_require_legacy_code() {
    let f = Fixture::new().await;
    let result = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: "无编码角色".into(),
            description: None,
        },
    )
    .await;
    let role = result.expect("角色创建不应要求历史编码");
    assert!(role.get("code").is_none());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_validation_project_description_limit_matches_create() {
    let f = Fixture::new().await;
    let p = service::project::detail(&f.state.db, &f.admin, f.project_id)
        .await
        .unwrap();
    let result = service::project::update(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::ProjectUpsert {
            name: "更新".into(),
            description: Some("文".repeat(501)),
            supplier_id: p["supplierId"].as_u64().unwrap(),
        },
    )
    .await;
    assert!(
        matches!(result, Err(crate::error::AppError::BadRequest(_))),
        "update must enforce the same limit: {result:?}"
    );
}

async fn message_commit_barrier(action: &str) {
    use sea_orm::{
        ConnectionTrait, DbBackend, PaginatorTrait, QuerySelect, Statement, TransactionTrait,
    };
    let f = Fixture::new().await;
    let txn = f.state.db.begin().await.unwrap();
    crate::entity::projects::Entity::find_by_id(f.project_id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap();
    let blocker: u64 = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let state = f.state.clone();
    let user = f.member.clone();
    let pid = f.project_id;
    let rid = f.round_id;
    let pending = tokio::spawn(async move {
        service::message::create(
            &state.db,
            &user,
            pid,
            &service::message::MessageCreate {
                round_id: Some(rid),
                content: "锁屏障留言".into(),
            },
            "http://localhost",
        )
        .await
    });
    let observed = tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let row=f.state.db.query_one(Statement::from_string(DbBackend::MySql,format!("SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX b ON b.trx_id=w.blocking_trx_id WHERE b.trx_mysql_thread_id={blocker}"))).await.unwrap().unwrap();
            if row.try_get::<i64>("","n").unwrap()>0 {break;}
            tokio::time::sleep(std::time::Duration::from_millis(250)).await;
        }
    }).await;
    if observed.is_err() {
        txn.rollback().await.unwrap();
        let _ = pending.await;
        panic!("message never reached owned lock barrier");
    }
    let sql = match action {
        "project" => format!("UPDATE projects SET status='COMPLETED' WHERE id={pid}"),
        "round" => format!("UPDATE rounds SET status='CONFIRMED' WHERE id={rid}"),
        _ => format!(
            "DELETE FROM project_members WHERE project_id={pid} AND user_id={}",
            f.member.id
        ),
    };
    txn.execute(Statement::from_string(DbBackend::MySql, sql))
        .await
        .unwrap();
    txn.commit().await.unwrap();
    let result = pending.await.unwrap();
    assert!(
        result.is_err(),
        "{action} committed first but message was accepted: {result:?}"
    );
    assert_eq!(
        crate::entity::messages::Entity::find()
            .filter(crate::entity::messages::Column::ProjectId.eq(pid))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
    assert_eq!(
        crate::entity::email_outbox::Entity::find()
            .filter(crate::entity::email_outbox::Column::ProjectId.eq(pid))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_message_rechecks_completed_project() {
    message_commit_barrier("project").await;
}
#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_message_rechecks_closed_round() {
    message_commit_barrier("round").await;
}
#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_message_rechecks_removed_member() {
    message_commit_barrier("member").await;
}

async fn await_owned_lock(db: &sea_orm::DatabaseConnection, blocker: u64) {
    use sea_orm::{ConnectionTrait, DbBackend, Statement};
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let row = db.query_one(Statement::from_string(DbBackend::MySql, format!("SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX b ON b.trx_id=w.blocking_trx_id WHERE b.trx_mysql_thread_id={blocker}"))).await.unwrap().unwrap();
            if row.try_get::<i64>("", "n").unwrap() > 0 { break; }
            tokio::time::sleep(std::time::Duration::from_millis(250)).await;
        }
    }).await.expect("request must reach the owned row lock");
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_rbac_role_disable_cannot_race_user_enable() {
    use sea_orm::{ConnectionTrait, DbBackend, QuerySelect, Statement, TransactionTrait};
    let f = Fixture::new().await;
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: "并发角色".into(),
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
            role_ids: vec![role],
        },
    )
    .await
    .unwrap();
    service::user::set_status(&f.state.db, &f.admin, f.member.id, "DISABLED")
        .await
        .unwrap();
    let txn = f.state.db.begin().await.unwrap();
    users::Entity::find_by_id(f.member.id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let (db, admin, uid) = (f.state.db.clone(), f.admin.clone(), f.member.id);
    let enable =
        tokio::spawn(async move { service::user::set_status(&db, &admin, uid, "ACTIVE").await });
    await_owned_lock(&f.state.db, blocker).await;
    let (db, admin) = (f.state.db.clone(), f.admin.clone());
    let mut disable =
        tokio::spawn(async move { service::role::set_status(&db, &admin, role, "DISABLED").await });
    // Old code commits the conflicting mutation. Serialized code must wait until release.
    let early = tokio::time::timeout(std::time::Duration::from_secs(1), &mut disable).await;
    txn.commit().await.unwrap();
    let a = enable.await.unwrap();
    let b = match early {
        Ok(r) => r.unwrap(),
        Err(_) => disable.await.unwrap(),
    };
    let user = users::Entity::find_by_id(uid)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let role = roles::Entity::find_by_id(role)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert!(
        !(user.status == CommonStatus::Active && role.status == CommonStatus::Disabled),
        "enable={a:?}, disable={b:?}"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_user_role_assignment_rechecks_role_disabled_after_wait() {
    use crate::entity::{audit_logs, user_roles};
    use sea_orm::{
        ConnectionTrait, DbBackend, PaginatorTrait, QueryOrder, Statement, TransactionTrait,
    };
    let f = Fixture::new().await;
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: "等待后禁用角色".into(),
            description: None,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    let before = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.eq(f.member.id))
        .order_by_asc(user_roles::Column::RoleId)
        .all(&f.state.db)
        .await
        .unwrap();
    let before_audit = audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.eq("USER_ASSIGN_ROLE"))
        .filter(audit_logs::Column::TargetId.eq(f.member.id.to_string()))
        .count(&f.state.db)
        .await
        .unwrap();

    let txn = f.state.db.begin().await.unwrap();
    service::perm::lock_management_state(&txn).await.unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let (db, admin, uid) = (f.state.db.clone(), f.admin.clone(), f.member.id);
    let pending = tokio::spawn(async move {
        service::user::assign_roles(
            &db,
            &admin,
            uid,
            &service::user::RoleAssign {
                role_ids: vec![role],
            },
        )
        .await
    });
    await_owned_lock(&f.state.db, blocker).await;
    txn.execute(Statement::from_string(
        DbBackend::MySql,
        format!("UPDATE roles SET status='DISABLED' WHERE id={role}"),
    ))
    .await
    .unwrap();
    txn.commit().await.unwrap();

    assert!(pending.await.unwrap().is_err());
    let after = user_roles::Entity::find()
        .filter(user_roles::Column::UserId.eq(f.member.id))
        .order_by_asc(user_roles::Column::RoleId)
        .all(&f.state.db)
        .await
        .unwrap();
    let after_audit = audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.eq("USER_ASSIGN_ROLE"))
        .filter(audit_logs::Column::TargetId.eq(f.member.id.to_string()))
        .count(&f.state.db)
        .await
        .unwrap();
    assert_eq!(after, before);
    assert_eq!(after_audit, before_audit);
    assert_eq!(
        roles::Entity::find_by_id(role)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .status,
        CommonStatus::Disabled
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_department_concurrent_moves_cannot_form_cycle() {
    use crate::entity::departments;
    use sea_orm::{ConnectionTrait, DbBackend, QuerySelect, Statement, TransactionTrait};
    let f = Fixture::new().await;
    let req = service::dept::DeptUpsert {
        name: "并发部门".into(),
        parent_id: None,
        sort_no: Some(0),
    };
    let a = service::dept::create(&f.state.db, &f.admin, &req)
        .await
        .unwrap()["id"]
        .as_u64()
        .unwrap();
    let b = service::dept::create(&f.state.db, &f.admin, &req)
        .await
        .unwrap()["id"]
        .as_u64()
        .unwrap();
    let txn = f.state.db.begin().await.unwrap();
    departments::Entity::find_by_id(a)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let (db, admin) = (f.state.db.clone(), f.admin.clone());
    let first = tokio::spawn(async move {
        service::dept::update(
            &db,
            &admin,
            a,
            &service::dept::DeptUpsert {
                name: "A".into(),
                parent_id: Some(b),
                sort_no: None,
            },
        )
        .await
    });
    await_owned_lock(&f.state.db, blocker).await;
    let (db, admin) = (f.state.db.clone(), f.admin.clone());
    let mut second = tokio::spawn(async move {
        service::dept::update(
            &db,
            &admin,
            b,
            &service::dept::DeptUpsert {
                name: "B".into(),
                parent_id: Some(a),
                sort_no: None,
            },
        )
        .await
    });
    let early = tokio::time::timeout(std::time::Duration::from_secs(1), &mut second).await;
    txn.commit().await.unwrap();
    let one = first.await.unwrap();
    let two = match early {
        Ok(r) => r.unwrap(),
        Err(_) => second.await.unwrap(),
    };
    let ar = departments::Entity::find_by_id(a)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let br = departments::Entity::find_by_id(b)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert!(
        !(ar.parent_id == Some(b) && br.parent_id == Some(a)),
        "first={one:?}, second={two:?}"
    );
}

async fn last_admin_barrier(demote: bool) {
    use sea_orm::{ConnectionTrait, DbBackend, QuerySelect, Statement, TransactionTrait};
    let f = Fixture::new().await;
    let admin_role = roles::Entity::find()
        .filter(roles::Column::Name.eq("系统管理员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let staff_role = roles::Entity::find()
        .filter(roles::Column::Name.eq("内部成员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    service::user::assign_roles(
        &f.state.db,
        &f.admin,
        f.member.id,
        &service::user::RoleAssign {
            role_ids: vec![admin_role.id],
        },
    )
    .await
    .unwrap();
    let txn = f.state.db.begin().await.unwrap();
    users::Entity::find_by_id(f.member.id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap();
    // Role changes do not update users; hold the target binding as well.
    crate::entity::user_roles::Entity::find_by_id((f.member.id, admin_role.id))
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let (db, admin, uid, rid) = (
        f.state.db.clone(),
        f.admin.clone(),
        f.member.id,
        staff_role.id,
    );
    let first = tokio::spawn(async move {
        if demote {
            service::user::assign_roles(
                &db,
                &admin,
                uid,
                &service::user::RoleAssign {
                    role_ids: vec![rid],
                },
            )
            .await
        } else {
            service::user::set_status(&db, &admin, uid, "DISABLED")
                .await
                .map(|_| ())
        }
    });
    await_owned_lock(&f.state.db, blocker).await;
    let (db, actor, uid) = (f.state.db.clone(), f.member.clone(), f.admin.id);
    let mut second = tokio::spawn(async move {
        if demote {
            service::user::assign_roles(
                &db,
                &actor,
                uid,
                &service::user::RoleAssign {
                    role_ids: vec![rid],
                },
            )
            .await
        } else {
            service::user::set_status(&db, &actor, uid, "DISABLED")
                .await
                .map(|_| ())
        }
    });
    let early = tokio::time::timeout(std::time::Duration::from_secs(1), &mut second).await;
    txn.commit().await.unwrap();
    let a = first.await.unwrap();
    let b = match early {
        Ok(r) => r.unwrap(),
        Err(_) => second.await.unwrap(),
    };
    let n: i64 = f.state.db.query_one(Statement::from_string(DbBackend::MySql, "SELECT COUNT(*) n FROM users u JOIN user_roles ur ON ur.user_id=u.id JOIN roles r ON r.id=ur.role_id WHERE r.is_built_in=1 AND r.name='系统管理员' AND u.status='ACTIVE'")).await.unwrap().unwrap().try_get("", "n").unwrap();
    // Restore the shared isolated fixture administrator before assertions/next test.
    f.state
        .db
        .execute(Statement::from_string(
            DbBackend::MySql,
            format!("UPDATE users SET status='ACTIVE' WHERE id={}", f.admin.id),
        ))
        .await
        .unwrap();
    f.state
        .db
        .execute(Statement::from_string(
            DbBackend::MySql,
            format!("DELETE FROM user_roles WHERE user_id={}", f.admin.id),
        ))
        .await
        .unwrap();
    f.state
        .db
        .execute(Statement::from_string(
            DbBackend::MySql,
            format!(
                "INSERT INTO user_roles(user_id,role_id) VALUES({},{})",
                f.admin.id, admin_role.id
            ),
        ))
        .await
        .unwrap();
    assert!(
        n >= 1,
        "all administrators removed: first={a:?}, second={b:?}"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_rbac_last_admin_concurrent_disable() {
    last_admin_barrier(false).await;
}
#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_rbac_last_admin_concurrent_demote() {
    last_admin_barrier(true).await;
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_department_input_boundaries() {
    let f = Fixture::new().await;
    let good = service::dept::DeptUpsert {
        name: "有效部门".into(),
        parent_id: None,
        sort_no: Some(0),
    };
    let id = service::dept::create(&f.state.db, &f.admin, &good)
        .await
        .unwrap()["id"]
        .as_u64()
        .unwrap();
    for (name, sort_no) in [
        (" ".to_string(), 0),
        ("文".repeat(65), 0),
        ("有效".into(), -1),
    ] {
        let req = service::dept::DeptUpsert {
            name,
            parent_id: None,
            sort_no: Some(sort_no),
        };
        assert!(matches!(
            service::dept::update(&f.state.db, &f.admin, id, &req).await,
            Err(crate::error::AppError::BadRequest(_))
        ));
        assert!(matches!(
            service::dept::create(&f.state.db, &f.admin, &req).await,
            Err(crate::error::AppError::BadRequest(_))
        ));
    }
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_auth_login_rechecks_locked_account() {
    use sea_orm::{ConnectionTrait, DbBackend, QuerySelect, Statement, TransactionTrait};
    let f = Fixture::new().await;
    let txn = f.state.db.begin().await.unwrap();
    users::Entity::find_by_id(f.member.id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let (state, employee_no) = (f.state.clone(), f.member.employee_no.clone());
    let pending = tokio::spawn(async move {
        service::auth::login(
            &state.db,
            &state.cfg,
            &crate::dto::LoginRequest {
                employee_no,
                password: "Regression123".into(),
                captcha_id: None,
                captcha_code: None,
            },
            None,
            &state.captchas,
        )
        .await
    });
    await_owned_lock(&f.state.db, blocker).await;
    txn.execute(Statement::from_string(DbBackend::MySql,format!("UPDATE users SET locked_until=DATE_ADD(UTC_TIMESTAMP(), INTERVAL 30 MINUTE) WHERE id={}",f.member.id))).await.unwrap();
    txn.commit().await.unwrap();
    assert!(
        pending.await.unwrap().is_err(),
        "waiting login must not clear a committed lock"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_auth_unknown_user_does_not_exhaust_pool() {
    let f = Fixture::new().await;
    let mut options = sea_orm::ConnectOptions::new(std::env::var("YF_TEST_DATABASE_URL").unwrap());
    options
        .max_connections(1)
        .min_connections(1)
        .acquire_timeout(std::time::Duration::from_secs(4));
    let db = sea_orm::Database::connect(options).await.unwrap();
    let result = tokio::time::timeout(
        std::time::Duration::from_secs(2),
        service::auth::login(
            &db,
            &f.state.cfg,
            &crate::dto::LoginRequest {
                employee_no: "no_such_regression_user".into(),
                password: "irrelevant".into(),
                captcha_id: None,
                captcha_code: None,
            },
            None,
            &f.state.captchas,
        ),
    )
    .await;
    assert!(
        matches!(result, Ok(Err(crate::error::AppError::Unauthorized(_)))),
        "unknown login must release its transaction before writing audit"
    );
}

async fn auth_change_barrier(refresh: bool) {
    use sea_orm::{ConnectionTrait, DbBackend, QuerySelect, Statement, TransactionTrait};
    let f = Fixture::new().await;
    let (_, token) = service::auth::login(
        &f.state.db,
        &f.state.cfg,
        &crate::dto::LoginRequest {
            employee_no: f.member.employee_no.clone(),
            password: "Regression123".into(),
            captcha_id: None,
            captcha_code: None,
        },
        None,
        &f.state.captchas,
    )
    .await
    .unwrap();
    let txn = f.state.db.begin().await.unwrap();
    users::Entity::find_by_id(f.member.id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let (state, user) = (f.state.clone(), f.member.clone());
    let pending = tokio::spawn(async move {
        if refresh {
            service::auth::refresh(&state.db, &state.cfg, &token, None)
                .await
                .map(|_| ())
        } else {
            service::auth::change_password(
                &state.db,
                &user,
                &crate::dto::ChangePasswordRequest {
                    old_password: "Regression123".into(),
                    new_password: "Changed123".into(),
                },
            )
            .await
        }
    });
    await_owned_lock(&f.state.db, blocker).await;
    if refresh {
        txn.execute(Statement::from_string(
            DbBackend::MySql,
            format!(
                "UPDATE users SET status='DISABLED' WHERE id={}",
                f.member.id
            ),
        ))
        .await
        .unwrap();
    } else {
        txn.execute(Statement::from_sql_and_values(
            DbBackend::MySql,
            "UPDATE users SET password_hash=?,must_change_password=true WHERE id=?",
            [
                crate::util::password::hash("Reset12345").unwrap().into(),
                f.member.id.into(),
            ],
        ))
        .await
        .unwrap();
    }
    txn.commit().await.unwrap();
    assert!(
        pending.await.unwrap().is_err(),
        "waiting authentication mutation ignored committed account change; refresh={refresh}"
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_auth_password_rechecks_admin_reset() {
    auth_change_barrier(false).await;
}
#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_auth_refresh_rechecks_disabled_user() {
    auth_change_barrier(true).await;
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_account_contact_and_employee_no_validation() {
    let f = Fixture::new().await;
    for payload in [serde_json::json!({"email":"not-an-email"})] {
        let req = serde_json::from_value(payload).unwrap();
        assert!(matches!(
            service::user::update(&f.state.db, &f.admin, f.member.id, &req).await,
            Err(crate::error::AppError::BadRequest(_))
        ));
    }
    let role = roles::Entity::find()
        .filter(roles::Column::Name.eq("内部成员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let bad = service::user::UserCreate {
        employee_no: "bad employee no".into(),
        password: "Regression123".into(),
        real_name: "测试".into(),
        email: "test@example.invalid".into(),
        department_id: None,
        role_id: Some(role.id),
        role_ids: None,
    };
    assert!(matches!(
        service::user::create(&f.state.db, &f.admin, &bad).await,
        Err(crate::error::AppError::BadRequest(_))
    ));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_upload_filename_validation() {
    let f = Fixture::new().await;
    for name in [
        format!("{}.pdf", "X".repeat(252)),
        "../file.pdf".into(),
        "bad\nname.pdf".into(),
    ] {
        let result = service::upload::init(
            &f.state,
            &f.member,
            &service::upload::InitReq {
                project_id: f.project_id,
                round_id: f.round_id,
                file_name: name,
                file_size: 4,
                file_md5: None,
            },
        )
        .await;
        assert!(matches!(result, Err(crate::error::AppError::BadRequest(_))));
    }
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn full_messages_cursor_survives_earlier_deletion() {
    let f = Fixture::new().await;
    for i in 0..25 {
        service::message::create(
            &f.state.db,
            &f.member,
            f.project_id,
            &service::message::MessageCreate {
                content: format!("分页 {i}"),
                round_id: None,
            },
            "http://localhost",
        )
        .await
        .unwrap();
    }
    let q = serde_json::from_value(serde_json::json!({"page":1,"pageSize":20})).unwrap();
    let first = service::message::list(&f.state.db, &f.admin, f.project_id, &q)
        .await
        .unwrap();
    let data = serde_json::to_value(first).unwrap();
    let rows = data["list"].as_array().unwrap();
    service::message::delete(&f.state.db, &f.admin, rows[0]["id"].as_u64().unwrap())
        .await
        .unwrap();
    let q = serde_json::from_value(
        serde_json::json!({"page":2,"pageSize":20,"beforeId":rows.last().unwrap()["id"]}),
    )
    .unwrap();
    let second = service::message::list(&f.state.db, &f.admin, f.project_id, &q)
        .await
        .unwrap();
    assert_eq!(
        serde_json::to_value(second).unwrap()["list"]
            .as_array()
            .unwrap()
            .len(),
        5,
        "deletion before cursor must not skip an older message"
    );
}
