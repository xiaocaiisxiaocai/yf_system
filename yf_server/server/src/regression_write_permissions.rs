//! Transaction-final authorization regressions. Run only through scripts/test-isolated.py.

use crate::{
    entity::{audit_logs, permissions, projects, role_permissions, rounds},
    error::AppError,
    regression::Fixture,
    service,
};
use sea_orm::{
    ColumnTrait, ConnectionTrait, DatabaseTransaction, DbBackend, EntityTrait, PaginatorTrait,
    QueryFilter, QuerySelect, Statement, TransactionTrait,
};

async fn grant_only(f: &Fixture, code: &str) -> u64 {
    let permission = permissions::Entity::find()
        .filter(permissions::Column::Code.eq(code))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("写入终检-{code}-{}", f.member.id),
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
            permission_ids: vec![permission.id],
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
    service::perm::check_perm(&f.state.db, f.member.id, code)
        .await
        .unwrap();
    role
}

async fn gate_and_project(f: &Fixture) -> (DatabaseTransaction, u64) {
    let txn = f.state.db.begin().await.unwrap();
    service::perm::lock_management_state(&txn).await.unwrap();
    projects::Entity::find_by_id(f.project_id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap()
        .unwrap();
    let blocker = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id".to_owned(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    (txn, blocker)
}

async fn await_owned_lock(db: &sea_orm::DatabaseConnection, blocker: u64) {
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let row = db.query_one(Statement::from_string(DbBackend::MySql, format!("SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX b ON b.trx_id=w.blocking_trx_id WHERE b.trx_mysql_thread_id={blocker}"))).await.unwrap().unwrap();
            if row.try_get::<i64>("", "n").unwrap() > 0 {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        }
    })
    .await
    .expect("business writer never reached the owned lock barrier");
}

async fn revoke_and_release(txn: DatabaseTransaction, role: u64) {
    role_permissions::Entity::delete_many()
        .filter(role_permissions::Column::RoleId.eq(role))
        .exec(&txn)
        .await
        .unwrap();
    txn.commit().await.unwrap();
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_round_create_rechecks_after_lock_wait() {
    let f = Fixture::new().await;
    let role = grant_only(&f, "round:create").await;
    let before = rounds::Entity::find()
        .filter(rounds::Column::ProjectId.eq(f.project_id))
        .count(&f.state.db)
        .await
        .unwrap();
    let (txn, blocker) = gate_and_project(&f).await;
    let (db, actor, project_id) = (f.state.db.clone(), f.member.clone(), f.project_id);
    let pending = tokio::spawn(async move {
        service::round::create(
            &db,
            &actor,
            project_id,
            &service::round::RoundCreate {
                title: Some("权限撤销后不得创建".into()),
                remark: None,
                confirm_side: "COMPANY".into(),
            },
        )
        .await
    });
    await_owned_lock(&f.state.db, blocker).await;
    revoke_and_release(txn, role).await;
    assert!(matches!(pending.await.unwrap(), Err(AppError::Forbidden)));
    assert_eq!(
        rounds::Entity::find()
            .filter(rounds::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        before
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_message_create_rechecks_before_message_outbox_and_audit() {
    let f = Fixture::new().await;
    let role = grant_only(&f, "message:create").await;
    let (txn, blocker) = gate_and_project(&f).await;
    let (db, actor, project_id, round_id) = (
        f.state.db.clone(),
        f.member.clone(),
        f.project_id,
        f.round_id,
    );
    let pending = tokio::spawn(async move {
        service::message::create(
            &db,
            &actor,
            project_id,
            &service::message::MessageCreate {
                content: "权限撤销后不得留言".into(),
                round_id: Some(round_id),
            },
            "http://localhost",
        )
        .await
    });
    await_owned_lock(&f.state.db, blocker).await;
    revoke_and_release(txn, role).await;
    assert!(matches!(pending.await.unwrap(), Err(AppError::Forbidden)));
    assert_eq!(
        crate::entity::messages::Entity::find()
            .filter(crate::entity::messages::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
    assert_eq!(
        crate::entity::email_outbox::Entity::find()
            .filter(crate::entity::email_outbox::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
    assert_eq!(
        audit_logs::Entity::find()
            .filter(audit_logs::Column::Action.eq("MESSAGE_CREATE"))
            .filter(audit_logs::Column::TargetType.eq("message"))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_chunk_upload_rechecks_after_lock_wait() {
    let f = Fixture::new().await;
    let role = grant_only(&f, "file:upload").await;
    let sid = f.init("revoked-chunk.pdf").await;
    let (txn, blocker) = gate_and_project(&f).await;
    let (state, actor, sid_for_write) = (f.state.clone(), f.member.clone(), sid.clone());
    let pending = tokio::spawn(async move {
        service::upload::put_chunk(&state, &actor, &sid_for_write, 0, b"test").await
    });
    await_owned_lock(&f.state.db, blocker).await;
    revoke_and_release(txn, role).await;
    assert!(matches!(pending.await.unwrap(), Err(AppError::Forbidden)));
    assert!(!crate::storage::chunk_path(&f.state.cfg.storage.root, &sid, 0).exists());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn write_permission_merge_rechecks_before_file_outbox_and_audit() {
    let f = Fixture::new().await;
    let role = grant_only(&f, "file:upload").await;
    let sid = f.init("revoked-merge.pdf").await;
    service::upload::put_chunk(&f.state, &f.member, &sid, 0, b"test")
        .await
        .unwrap();
    let (txn, blocker) = gate_and_project(&f).await;
    let (state, actor, sid_for_merge) = (f.state.clone(), f.member.clone(), sid.clone());
    let pending =
        tokio::spawn(async move { service::upload::merge(&state, &actor, &sid_for_merge).await });
    await_owned_lock(&f.state.db, blocker).await;
    revoke_and_release(txn, role).await;
    assert!(matches!(pending.await.unwrap(), Err(AppError::Forbidden)));
    assert_eq!(
        crate::entity::files::Entity::find()
            .filter(crate::entity::files::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
    assert_eq!(
        crate::entity::email_outbox::Entity::find()
            .filter(crate::entity::email_outbox::Column::ProjectId.eq(f.project_id))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn business_gate_is_shared_but_still_blocks_management_changes() {
    let f = Fixture::new().await;
    let first = f.state.db.begin().await.unwrap();
    service::perm::lock_business_state(&first).await.unwrap();
    let blocker = first
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id".to_owned(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();

    let db = f.state.db.clone();
    let second = tokio::spawn(async move {
        let txn = db.begin().await.unwrap();
        service::perm::lock_business_state(&txn).await.unwrap();
        txn.commit().await.unwrap();
    });
    tokio::time::timeout(std::time::Duration::from_secs(1), second)
        .await
        .expect("a second business transaction was blocked by the shared gate")
        .unwrap();

    let db = f.state.db.clone();
    let mut management = tokio::spawn(async move {
        let txn = db.begin().await.unwrap();
        service::perm::lock_management_state(&txn).await.unwrap();
        txn.commit().await.unwrap();
    });
    await_owned_lock(&f.state.db, blocker).await;
    assert!(
        tokio::time::timeout(std::time::Duration::from_millis(200), &mut management)
            .await
            .is_err(),
        "management acquired its exclusive gate before the business reader released it"
    );
    first.commit().await.unwrap();
    management.await.unwrap();
}
