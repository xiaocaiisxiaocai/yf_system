//! Project activity regressions run only through scripts/test-isolated.py.

use std::collections::HashSet;

use chrono::{Duration, Timelike, Utc};
use migration::MigratorTrait;
use sea_orm::{
    ActiveModelTrait, ColumnTrait, ConnectionTrait, DbBackend, EntityTrait, PaginatorTrait,
    QueryFilter, QueryOrder, Set, Statement, TransactionTrait,
};
use serde_json::{json, Value};

use crate::{
    entity::{
        audit_logs,
        enums::{CommonStatus, ConfirmSide, MessageStatus, RoundStatus},
        files, messages, project_activities, projects, roles, rounds, users,
    },
    error::AppError,
    middleware::auth::CurrentUser,
    regression::Fixture,
    service,
};

fn activity_query(
    activity_type: Option<&str>,
    cursor: Option<String>,
    page_size: u64,
) -> service::project_activity::ActivityQuery {
    serde_json::from_value(json!({
        "type": activity_type,
        "cursor": cursor,
        "pageSize": page_size,
    }))
    .unwrap()
}

fn file_target_query(target_id: u64) -> service::file::FileListQuery {
    serde_json::from_value(json!({
        "page": 1,
        "pageSize": 20,
        "targetId": target_id,
    }))
    .unwrap()
}

fn message_target_query(target_id: u64) -> service::message::MessageListQuery {
    serde_json::from_value(json!({
        "page": 1,
        "pageSize": 20,
        "targetId": target_id,
    }))
    .unwrap()
}

fn list(value: &Value) -> &[Value] {
    value["list"].as_array().unwrap()
}

async fn activities(f: &Fixture, user: &CurrentUser) -> Value {
    service::project_activity::list(
        &f.state.db,
        user,
        f.project_id,
        &activity_query(None, None, 50),
    )
    .await
    .unwrap()
}

async fn create_internal_user(f: &Fixture, real_name: &str) -> CurrentUser {
    let role = roles::Entity::find()
        .filter(roles::Column::Name.eq("内部成员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let employee_no = format!("a{}", &uuid::Uuid::new_v4().simple().to_string()[..24]);
    let value = service::user::create(
        &f.state.db,
        &f.admin,
        &service::user::UserCreate {
            employee_no: employee_no.clone(),
            password: "Regression123".into(),
            real_name: real_name.into(),
            email: format!("{employee_no}@example.invalid"),
            department_id: None,
            role_id: Some(role.id),
            role_ids: None,
        },
    )
    .await
    .unwrap();
    CurrentUser {
        id: value["id"].as_u64().unwrap(),
        employee_no,
        user_type: crate::entity::enums::UserType::Internal,
        supplier_id: None,
    }
}

async fn create_supplier_user(f: &Fixture, supplier_id: u64) -> CurrentUser {
    let employee_no = format!("s{}", &uuid::Uuid::new_v4().simple().to_string()[..24]);
    let value = service::supplier::create_account(
        &f.state.db,
        &f.admin,
        supplier_id,
        &service::supplier::AccountCreate {
            employee_no: employee_no.clone(),
            password: "Regression123".into(),
            real_name: "动态供应商账号".into(),
            email: format!("{employee_no}@example.invalid"),
        },
    )
    .await
    .unwrap();
    CurrentUser {
        id: value["id"].as_u64().unwrap(),
        employee_no,
        user_type: crate::entity::enums::UserType::Supplier,
        supplier_id: Some(supplier_id),
    }
}

async fn create_draft(f: &Fixture, name: &str) -> u64 {
    let supplier_id = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .supplier_id;
    service::project::create(
        &f.state.db,
        &f.admin,
        &service::project::ProjectUpsert {
            name: name.into(),
            description: None,
            supplier_id,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap()
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn project_activity_captures_four_business_types_and_project_status() {
    let f = Fixture::new().await;
    let message_id = service::message::create(
        &f.state.db,
        &f.member,
        f.project_id,
        &service::message::MessageCreate {
            content: "四类动态留言".into(),
            round_id: Some(f.round_id),
        },
        &f.state.cfg.web.base_url,
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    let sid = f.init("activity-four-types.pdf").await;
    service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test")
        .await
        .unwrap();
    let file_id = service::upload::merge(&f.state, &f.member, &sid)
        .await
        .unwrap()["id"]
        .as_u64()
        .unwrap();
    service::round::confirm(&f.state.db, &f.state.cfg, &f.admin, f.round_id)
        .await
        .unwrap();
    let rejected_round = service::round::create(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::round::RoundCreate {
            title: Some("待驳回轮次".into()),
            remark: None,
            confirm_side: "COMPANY".into(),
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    let rejection_reason = "尺寸证据不完整";
    service::round::reject(
        &f.state.db,
        &f.state.cfg,
        &f.admin,
        rejected_round,
        &service::round::RejectReq {
            reason: Some(rejection_reason.into()),
        },
    )
    .await
    .unwrap();
    let cancelled_round = service::round::create(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::round::RoundCreate {
            title: Some("待撤销轮次".into()),
            remark: None,
            confirm_side: "COMPANY".into(),
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    service::round::cancel(&f.state.db, &f.state.cfg, &f.admin, cancelled_round)
        .await
        .unwrap();
    service::project::set_status(
        &f.state.db,
        &f.admin,
        f.project_id,
        &service::project::StatusChange {
            status: "COMPLETED".into(),
        },
    )
    .await
    .unwrap();

    let response = activities(&f, &f.member).await;
    let rows = list(&response);
    for (activity_type, action) in [
        ("PROJECT", "START"),
        ("PROJECT", "COMPLETE"),
        ("ROUND", "CREATE"),
        ("ROUND", "CONFIRM"),
        ("ROUND", "REJECT"),
        ("ROUND", "CANCEL"),
        ("FILE", "UPLOAD"),
        ("MESSAGE", "CREATE"),
    ] {
        let item = rows
            .iter()
            .find(|item| item["type"] == activity_type && item["action"] == action)
            .unwrap_or_else(|| panic!("missing {activity_type}/{action}"));
        let occurred_at: chrono::DateTime<Utc> =
            serde_json::from_value(item["occurredAt"].clone()).unwrap();
        assert!(occurred_at <= Utc::now() + Duration::seconds(1));
        assert!(item["id"].as_u64().unwrap() > 0);
        assert!(!item["actorName"].as_str().unwrap().trim().is_empty());
        assert!(!item["title"].as_str().unwrap().trim().is_empty());
        assert_eq!(item["targetAvailable"], true);
    }
    let file = rows
        .iter()
        .find(|item| item["type"] == "FILE" && item["targetId"] == file_id)
        .unwrap();
    assert_eq!(file["actorName"], "测试成员");
    assert_eq!(file["roundId"], f.round_id);
    assert_eq!(file["roundNo"], 1);
    let message = rows
        .iter()
        .find(|item| item["type"] == "MESSAGE" && item["targetId"] == message_id)
        .unwrap();
    assert_eq!(message["actorName"], "测试成员");
    assert_eq!(message["roundId"], f.round_id);
    assert_eq!(message["roundNo"], 1);
    let admin_name = users::Entity::find_by_id(f.admin.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .real_name;
    let confirmed = rows
        .iter()
        .find(|item| item["type"] == "ROUND" && item["action"] == "CONFIRM")
        .unwrap();
    assert_eq!(confirmed["actorName"], admin_name);
    assert_eq!(confirmed["targetId"], f.round_id);
    let rejected = rows
        .iter()
        .find(|item| item["type"] == "ROUND" && item["action"] == "REJECT")
        .unwrap();
    assert_eq!(rejected["actorName"], admin_name);
    assert_eq!(rejected["targetId"], rejected_round);
    assert_eq!(rejected["summary"], rejection_reason);
    let cancelled = rows
        .iter()
        .find(|item| item["type"] == "ROUND" && item["action"] == "CANCEL")
        .unwrap();
    assert_eq!(cancelled["actorName"], admin_name);
    assert_eq!(cancelled["targetId"], cancelled_round);
    assert_eq!(response["summary"]["status"], "COMPLETED");
    assert_eq!(response["summary"]["pendingRounds"], 0);
    assert_eq!(response["summary"]["lastActivityAt"], rows[0]["occurredAt"]);
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn project_activity_enforces_scope_without_log_permission_and_never_leaks_audit_data() {
    let f = Fixture::new().await;
    assert!(
        service::perm::check_perm(&f.state.db, f.member.id, "log:view")
            .await
            .is_err()
    );
    assert!(!list(&activities(&f, &f.member).await).is_empty());

    let outsider = create_internal_user(&f, "非项目成员").await;
    assert!(matches!(
        service::project_activity::list(
            &f.state.db,
            &outsider,
            f.project_id,
            &activity_query(None, None, 20),
        )
        .await,
        Err(AppError::OutOfScope)
    ));

    let other_supplier = service::supplier::create(
        &f.state.db,
        &f.admin,
        &service::supplier::SupplierUpsert {
            name: format!("其它动态供应商-{}", uuid::Uuid::new_v4()),
            remark: None,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    let other_supplier_user = create_supplier_user(&f, other_supplier).await;
    assert!(matches!(
        service::project_activity::list(
            &f.state.db,
            &other_supplier_user,
            f.project_id,
            &activity_query(None, None, 20),
        )
        .await,
        Err(AppError::OutOfScope)
    ));

    let project_supplier = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .supplier_id;
    let disabled_user = create_supplier_user(&f, project_supplier).await;
    assert!(service::project_activity::list(
        &f.state.db,
        &disabled_user,
        f.project_id,
        &activity_query(None, None, 20),
    )
    .await
    .is_ok());
    let mut disabled: users::ActiveModel = users::Entity::find_by_id(disabled_user.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .into();
    disabled.status = Set(CommonStatus::Disabled);
    disabled.update(&f.state.db).await.unwrap();
    assert!(matches!(
        service::project_activity::list(
            &f.state.db,
            &disabled_user,
            f.project_id,
            &activity_query(None, None, 20),
        )
        .await,
        Err(AppError::Forbidden)
    ));

    let secret_path = "private/storage/8aa4f6b4.bin";
    let secret_ip = "198.51.100.77";
    service::audit::insert(
        &f.state.db,
        Some(f.member.id),
        Some(f.member.employee_no.clone()),
        "PROJECT_UPDATE",
        Some("project"),
        Some(f.project_id.to_string()),
        Some(json!({ "storagePath": secret_path, "rawDetail": "never expose" })),
        Some(secret_ip.into()),
    )
    .await
    .unwrap();
    let response = activities(&f, &f.member).await;
    let serialized = response.to_string();
    assert!(!serialized.contains(secret_path));
    assert!(!serialized.contains(secret_ip));
    assert!(!serialized.contains("rawDetail"));
    let expected_keys: HashSet<&str> = HashSet::from([
        "id",
        "type",
        "action",
        "actorName",
        "occurredAt",
        "title",
        "summary",
        "roundId",
        "roundNo",
        "targetId",
        "targetAvailable",
    ]);
    for item in list(&response) {
        let actual: HashSet<&str> = item
            .as_object()
            .unwrap()
            .keys()
            .map(String::as_str)
            .collect();
        assert_eq!(actual, expected_keys);
    }
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn project_activity_cursor_is_stable_for_equal_times_and_filters_preserve_summary() {
    let f = Fixture::new().await;
    let same_time = (Utc::now() + Duration::days(1)).with_nanosecond(0).unwrap();
    for index in 0..55 {
        project_activities::ActiveModel {
            project_id: Set(f.project_id),
            activity_type: Set(if index % 2 == 0 { "FILE" } else { "MESSAGE" }.into()),
            action: Set("CREATE".into()),
            actor_id: Set(Some(f.admin.id)),
            actor_name: Set("同秒操作者".into()),
            occurred_at: Set(same_time),
            title: Set(format!("同秒动态 {index}")),
            summary: Set(None),
            round_id: Set(None),
            round_no: Set(None),
            target_id: Set(None),
            source_key: Set(format!("cursor-fixture:{}:{index}", f.project_id)),
            ..Default::default()
        }
        .insert(&f.state.db)
        .await
        .unwrap();
    }
    let expected: Vec<u64> = project_activities::Entity::find()
        .filter(project_activities::Column::ProjectId.eq(f.project_id))
        .order_by_desc(project_activities::Column::OccurredAt)
        .order_by_desc(project_activities::Column::Id)
        .all(&f.state.db)
        .await
        .unwrap()
        .into_iter()
        .map(|item| item.id)
        .collect();

    let mut cursor = None;
    let mut actual = Vec::new();
    loop {
        let page = service::project_activity::list(
            &f.state.db,
            &f.member,
            f.project_id,
            &activity_query(None, cursor.take(), 7),
        )
        .await
        .unwrap();
        actual.extend(list(&page).iter().map(|item| item["id"].as_u64().unwrap()));
        cursor = page["nextCursor"].as_str().map(str::to_owned);
        if cursor.is_none() {
            break;
        }
        assert!(actual.len() <= expected.len(), "cursor did not converge");
    }
    assert_eq!(
        actual, expected,
        "equal timestamps must not duplicate or skip rows"
    );
    assert_eq!(
        actual.iter().copied().collect::<HashSet<_>>().len(),
        actual.len()
    );

    let unfiltered = service::project_activity::list(
        &f.state.db,
        &f.member,
        f.project_id,
        &activity_query(None, None, u64::MAX),
    )
    .await
    .unwrap();
    assert_eq!(list(&unfiltered).len(), 50, "pageSize must be capped at 50");
    let filtered = service::project_activity::list(
        &f.state.db,
        &f.member,
        f.project_id,
        &activity_query(Some("file"), None, 50),
    )
    .await
    .unwrap();
    assert!(list(&filtered).iter().all(|item| item["type"] == "FILE"));
    assert_eq!(filtered["summary"], unfiltered["summary"]);
    assert!(matches!(
        service::project_activity::list(
            &f.state.db,
            &f.member,
            f.project_id,
            &activity_query(None, Some("not-a-cursor".into()), 20),
        )
        .await,
        Err(AppError::BadRequest(_))
    ));
    assert!(matches!(
        service::project_activity::list(
            &f.state.db,
            &f.member,
            f.project_id,
            &activity_query(None, Some("ffffffffffffffffffffffffffffffff".into()), 20,),
        )
        .await,
        Err(AppError::BadRequest(_))
    ));
    assert!(matches!(
        service::project_activity::list(
            &f.state.db,
            &f.member,
            f.project_id,
            &activity_query(Some("audit"), None, 20),
        )
        .await,
        Err(AppError::BadRequest(_))
    ));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn project_activity_rolls_back_with_audit_but_survives_audit_cleanup() {
    let f = Fixture::new().await;
    let before = project_activities::Entity::find()
        .filter(project_activities::Column::ProjectId.eq(f.project_id))
        .count(&f.state.db)
        .await
        .unwrap();
    let txn = f.state.db.begin().await.unwrap();
    service::audit::insert(
        &txn,
        Some(f.admin.id),
        Some(f.admin.employee_no.clone()),
        "PROJECT_UPDATE",
        Some("project"),
        Some(f.project_id.to_string()),
        None,
        None,
    )
    .await
    .unwrap();
    assert_eq!(
        project_activities::Entity::find()
            .filter(project_activities::Column::ProjectId.eq(f.project_id))
            .count(&txn)
            .await
            .unwrap(),
        before + 1
    );
    txn.rollback().await.unwrap();
    assert_eq!(
        project_activities::Entity::find()
            .filter(project_activities::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        before
    );

    service::audit::insert(
        &f.state.db,
        Some(f.admin.id),
        Some(f.admin.employee_no.clone()),
        "PROJECT_UPDATE",
        Some("project"),
        Some(f.project_id.to_string()),
        None,
        None,
    )
    .await
    .unwrap();
    let audit = audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.eq("PROJECT_UPDATE"))
        .filter(audit_logs::Column::TargetId.eq(f.project_id.to_string()))
        .order_by_desc(audit_logs::Column::Id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let source_key = format!("audit:{}", audit.id);
    assert!(project_activities::Entity::find()
        .filter(project_activities::Column::SourceKey.eq(&source_key))
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
    assert_eq!(
        service::log::delete_ids(&f.state.db, &f.admin, &[audit.id])
            .await
            .unwrap(),
        1
    );
    assert!(audit_logs::Entity::find_by_id(audit.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
    assert!(project_activities::Entity::find()
        .filter(project_activities::Column::SourceKey.eq(source_key))
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn project_activity_deleted_targets_cannot_reopen_and_message_text_is_redacted() {
    let f = Fixture::new().await;
    let other_project = create_draft(&f, "动态目标隔离项目").await;
    let secret_message = "删除后绝不能出现在项目动态中的留言原文";
    let message_id = service::message::create(
        &f.state.db,
        &f.member,
        f.project_id,
        &service::message::MessageCreate {
            content: secret_message.into(),
            round_id: Some(f.round_id),
        },
        &f.state.cfg.web.base_url,
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap();
    let sid = f.init("deleted-activity-target.pdf").await;
    service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test")
        .await
        .unwrap();
    let file_id = service::upload::merge(&f.state, &f.member, &sid)
        .await
        .unwrap()["id"]
        .as_u64()
        .unwrap();

    let message_query = message_target_query(message_id);
    assert_eq!(
        service::message::list(&f.state.db, &f.member, f.project_id, &message_query)
            .await
            .unwrap()
            .list
            .len(),
        1
    );
    assert!(
        service::message::list(&f.state.db, &f.admin, other_project, &message_query)
            .await
            .unwrap()
            .list
            .is_empty()
    );
    let file_query = file_target_query(file_id);
    assert_eq!(
        service::file::list(&f.state.db, &f.member, f.project_id, &file_query)
            .await
            .unwrap()
            .list
            .len(),
        1
    );
    assert!(
        service::file::list(&f.state.db, &f.admin, other_project, &file_query)
            .await
            .unwrap()
            .list
            .is_empty()
    );

    service::message::delete(&f.state.db, &f.admin, message_id)
        .await
        .unwrap();
    service::file::delete(&f.state, &f.admin, file_id)
        .await
        .unwrap();
    assert!(
        service::message::list(&f.state.db, &f.member, f.project_id, &message_query)
            .await
            .unwrap()
            .list
            .is_empty()
    );
    assert!(
        service::file::list(&f.state.db, &f.member, f.project_id, &file_query)
            .await
            .unwrap()
            .list
            .is_empty()
    );

    let deleted_file = files::Entity::find_by_id(file_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let stored_path =
        std::path::Path::new(&f.state.cfg.storage.root).join(&deleted_file.storage_path);
    let mut expired: files::ActiveModel = deleted_file.into();
    expired.deleted_at = Set(Some(Utc::now() - Duration::days(31)));
    expired.update(&f.state.db).await.unwrap();
    service::gc::run_all(&f.state).await;
    assert!(files::Entity::find_by_id(file_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
    assert!(!stored_path.exists());

    let response = activities(&f, &f.member).await;
    assert!(!response.to_string().contains(secret_message));
    let message_events: Vec<&Value> = list(&response)
        .iter()
        .filter(|item| item["type"] == "MESSAGE" && item["targetId"].as_u64() == Some(message_id))
        .collect();
    assert_eq!(message_events.len(), 2);
    assert!(message_events
        .iter()
        .all(|item| item["targetAvailable"] == false && item["summary"].is_null()));
    let file_events: Vec<&Value> = list(&response)
        .iter()
        .filter(|item| item["type"] == "FILE" && item["targetId"].as_u64() == Some(file_id))
        .collect();
    assert_eq!(file_events.len(), 2);
    assert!(file_events
        .iter()
        .all(|item| item["targetAvailable"] == false));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn project_activity_012_backfill_is_idempotent_and_does_not_invent_round_transitions() {
    let f = Fixture::new().await;
    let url = std::env::var("YF_TEST_DATABASE_URL").expect("请使用隔离测试脚本");
    assert!(url
        .split('?')
        .next()
        .unwrap_or(&url)
        .rsplit('/')
        .next()
        .unwrap_or_default()
        .starts_with("yf_test_"));
    let migrations = migration::Migrator::migrations();
    let target = migrations
        .iter()
        .position(|item| item.name() == "m20260909_000012_project_activities")
        .expect("project activity migration must remain registered");
    migration::Migrator::down(&f.state.db, Some((migrations.len() - target) as u32))
        .await
        .unwrap();

    let known_created_at = Utc::now()
        .checked_sub_signed(Duration::days(365))
        .unwrap()
        .with_nanosecond(123_000_000)
        .unwrap();
    let legacy_round = rounds::ActiveModel {
        project_id: Set(f.project_id),
        round_no: Set(900_001),
        title: Set(Some("无状态日志历史轮次".into())),
        remark: Set(None),
        confirm_side: Set(ConfirmSide::Company),
        status: Set(RoundStatus::Confirmed),
        decided_by: Set(None),
        decided_at: Set(None),
        reject_reason: Set(None),
        created_by: Set(f.admin.id),
        created_at: Set(known_created_at),
        updated_at: Set(known_created_at),
        ..Default::default()
    }
    .insert(&f.state.db)
    .await
    .unwrap();
    let persisted_round = rounds::Entity::find_by_id(legacy_round.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        persisted_round.created_at.nanosecond(),
        0,
        "legacy rounds store their evidence timestamp at whole-second precision"
    );
    let legacy_secret = "迁移后不应复活的已删留言";
    let legacy_message = messages::ActiveModel {
        project_id: Set(f.project_id),
        round_id: Set(None),
        sender_id: Set(f.member.id),
        content: Set(legacy_secret.into()),
        status: Set(MessageStatus::Deleted),
        deleted_by: Set(Some(f.admin.id)),
        deleted_at: Set(Some(known_created_at + Duration::minutes(1))),
        created_at: Set(known_created_at),
        ..Default::default()
    }
    .insert(&f.state.db)
    .await
    .unwrap();

    migration::Migrator::up(&f.state.db, Some(1)).await.unwrap();
    let round_events = project_activities::Entity::find()
        .filter(project_activities::Column::RoundId.eq(legacy_round.id))
        .order_by_asc(project_activities::Column::Id)
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(round_events.len(), 1);
    assert_eq!(round_events[0].action, "CREATE");
    assert_eq!(round_events[0].occurred_at, persisted_round.created_at);
    assert_eq!(
        round_events[0].source_key,
        format!("round:{}:create", legacy_round.id)
    );
    let message_events = project_activities::Entity::find()
        .filter(project_activities::Column::TargetId.eq(legacy_message.id))
        .filter(project_activities::Column::ActivityType.eq("MESSAGE"))
        .all(&f.state.db)
        .await
        .unwrap();
    assert_eq!(message_events.len(), 2);
    assert!(message_events.iter().all(|item| item.summary.is_none()));

    let before = project_activities::Entity::find()
        .count(&f.state.db)
        .await
        .unwrap();
    f.state
        .db
        .execute(Statement::from_sql_and_values(
            DbBackend::MySql,
            "DELETE FROM seaql_migrations WHERE version = ?",
            ["m20260909_000012_project_activities".into()],
        ))
        .await
        .unwrap();
    migration::Migrator::up(&f.state.db, Some(1)).await.unwrap();
    let after = project_activities::Entity::find()
        .count(&f.state.db)
        .await
        .unwrap();
    let distinct = f
        .state
        .db
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT COUNT(DISTINCT source_key) AS n FROM project_activities",
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get::<i64>("", "n")
        .unwrap();
    assert_eq!(after, before);
    assert_eq!(distinct as u64, after);
    assert!(!project_activities::Entity::find()
        .filter(project_activities::Column::ProjectId.eq(f.project_id))
        .all(&f.state.db)
        .await
        .unwrap()
        .iter()
        .any(|item| item.summary.as_deref() == Some(legacy_secret)));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn project_activity_empty_draft_delete_removes_timeline_but_keeps_system_audit() {
    let f = Fixture::new().await;
    let project_id = create_draft(&f, "可删除空草稿").await;
    assert!(
        project_activities::Entity::find()
            .filter(project_activities::Column::ProjectId.eq(project_id))
            .count(&f.state.db)
            .await
            .unwrap()
            > 0
    );

    service::project::delete(&f.state, &f.admin, project_id)
        .await
        .unwrap();

    assert!(projects::Entity::find_by_id(project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
    assert_eq!(
        project_activities::Entity::find()
            .filter(project_activities::Column::ProjectId.eq(project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
    assert!(audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.eq("PROJECT_DELETE"))
        .filter(audit_logs::Column::TargetId.eq(project_id.to_string()))
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
}
