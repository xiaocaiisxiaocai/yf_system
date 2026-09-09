//! Deletion regression tests run only through scripts/test-isolated.py.
use crate::{
    entity::{audit_logs, projects},
    error::AppError,
    regression::Fixture,
    service,
};
use sea_orm::{
    ActiveModelTrait, ColumnTrait, ConnectionTrait, DbBackend, EntityTrait, PaginatorTrait,
    QueryFilter, QuerySelect, Set, Statement, TransactionTrait,
};

async fn draft(f: &Fixture) -> u64 {
    let existing = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    service::project::create(
        &f.state.db,
        &f.admin,
        &service::project::ProjectUpsert {
            name: "删除安全草稿".into(),
            description: None,
            supplier_id: existing.supplier_id,
        },
    )
    .await
    .unwrap()["id"]
        .as_u64()
        .unwrap()
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_project_requires_delete_permission_and_accepts_assigned_role() {
    let f = Fixture::new().await;
    let id = draft(&f).await;
    let manager = crate::entity::roles::Entity::find()
        .filter(crate::entity::roles::Column::Name.eq("项目管理员"))
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
        &f.admin,
        id,
        &service::project::MembersSet {
            user_ids: vec![f.member.id],
        },
    )
    .await
    .unwrap();
    let result = service::project::delete(&f.state, &f.member, id).await;
    assert!(
        matches!(result, Err(AppError::Forbidden)),
        "non-admin physically deleted an accessible project: {result:?}"
    );
    assert!(projects::Entity::find_by_id(id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());

    let permission = crate::entity::permissions::Entity::find()
        .filter(crate::entity::permissions::Column::Code.eq("project:delete"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let mut permission_ids: Vec<u64> = crate::entity::role_permissions::Entity::find()
        .filter(crate::entity::role_permissions::Column::RoleId.eq(manager.id))
        .all(&f.state.db)
        .await
        .unwrap()
        .into_iter()
        .map(|binding| binding.permission_id)
        .collect();
    permission_ids.push(permission.id);
    service::role::assign_permissions(
        &f.state.db,
        &f.admin,
        manager.id,
        &service::role::PermAssign { permission_ids },
    )
    .await
    .unwrap();
    service::project::delete(&f.state, &f.member, id)
        .await
        .unwrap();
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_project_rechecks_status_after_waiting_for_project_lock() {
    use crate::entity::enums::ProjectStatus;
    let f = Fixture::new().await;
    let id = draft(&f).await;
    let txn = f.state.db.begin().await.unwrap();
    let row = projects::Entity::find_by_id(id)
        .lock_exclusive()
        .one(&txn)
        .await
        .unwrap()
        .unwrap();
    let blocker: u64 = txn
        .query_one(Statement::from_string(
            DbBackend::MySql,
            "SELECT CONNECTION_ID() AS id".to_owned(),
        ))
        .await
        .unwrap()
        .unwrap()
        .try_get("", "id")
        .unwrap();
    let state = f.state.clone();
    let user = f.admin.clone();
    let pending = tokio::spawn(async move { service::project::delete(&state, &user, id).await });
    tokio::time::timeout(std::time::Duration::from_secs(12), async {
        loop {
            assert!(!pending.is_finished(), "delete did not wait for the locked project");
            let wait = f.state.db.query_one(Statement::from_string(DbBackend::MySql, format!("SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX b ON b.trx_id=w.blocking_trx_id WHERE b.trx_mysql_thread_id={blocker}"))).await.unwrap().unwrap();
            if wait.try_get::<i64>("", "n").unwrap() > 0 { break; }
            tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        }
    }).await.expect("delete must reach the row lock");
    let mut update: projects::ActiveModel = row.into();
    update.status = Set(ProjectStatus::InProgress);
    update.update(&txn).await.unwrap();
    txn.commit().await.unwrap();
    let result = pending.await.unwrap();
    assert!(
        matches!(result, Err(AppError::BadRequest(_))),
        "stale DRAFT check deleted a newly started project: {result:?}"
    );
    let kept = projects::Entity::find_by_id(id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(kept.status, ProjectStatus::InProgress);
    assert_eq!(
        audit_logs::Entity::find()
            .filter(audit_logs::Column::Action.eq("PROJECT_DELETE"))
            .filter(audit_logs::Column::TargetId.eq(id.to_string()))
            .count(&f.state.db)
            .await
            .unwrap(),
        0
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_log_cleanup_preserves_an_audit_record() {
    let f = Fixture::new().await;
    let row = audit_logs::Entity::find()
        .filter(audit_logs::Column::TargetId.eq(f.project_id.to_string()))
        .filter(audit_logs::Column::Action.eq("PROJECT_CREATE"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let count = service::log::delete_ids(&f.state.db, &f.admin, &[row.id, row.id, u64::MAX])
        .await
        .unwrap();
    assert_eq!(count, 1);
    let event = audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.eq("AUDIT_LOG_DELETE"))
        .all(&f.state.db)
        .await
        .unwrap()
        .into_iter()
        .find(|entry| {
            entry
                .detail
                .as_ref()
                .is_some_and(|d| d["ids"] == serde_json::json!([row.id]))
        })
        .expect("physical audit cleanup left no audit record");
    assert_eq!(event.user_id, Some(f.admin.id));
    assert_eq!(event.detail.as_ref().unwrap()["deleted"], 1);
    let kept = audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.eq("ROUND_CREATE"))
        .filter(audit_logs::Column::TargetId.eq(f.round_id.to_string()))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert!(matches!(
        service::log::delete_ids(&f.state.db, &f.admin, &[kept.id, event.id]).await,
        Err(AppError::BadRequest(_))
    ));
    assert!(
        audit_logs::Entity::find_by_id(kept.id)
            .one(&f.state.db)
            .await
            .unwrap()
            .is_some(),
        "protected cleanup record must roll back the whole batch"
    );
    assert!(audit_logs::Entity::find_by_id(event.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_log_view_permission_cannot_delete() {
    use crate::entity::permissions;
    let f = Fixture::new().await;
    let role = service::role::create(
        &f.state.db,
        &f.admin,
        &service::role::RoleUpsert {
            name: format!("只读日志{}", f.member.id),
            description: None,
        },
    )
    .await
    .unwrap();
    let role_id = role["id"].as_u64().unwrap();
    let permission = permissions::Entity::find()
        .filter(permissions::Column::Code.eq("log:view"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    service::role::assign_permissions(
        &f.state.db,
        &f.admin,
        role_id,
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
            role_ids: vec![role_id],
        },
    )
    .await
    .unwrap();
    assert!(
        service::perm::check_perm(&f.state.db, f.member.id, "log:view")
            .await
            .is_ok()
    );
    let row = audit_logs::Entity::find()
        .filter(audit_logs::Column::Action.eq("PROJECT_CREATE"))
        .filter(audit_logs::Column::TargetId.eq(f.project_id.to_string()))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert!(matches!(
        service::log::delete_ids(&f.state.db, &f.member, &[row.id]).await,
        Err(AppError::Forbidden)
    ));
    assert!(audit_logs::Entity::find_by_id(row.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
    let delete_permission = permissions::Entity::find()
        .filter(permissions::Column::Code.eq("log:delete"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    service::role::assign_permissions(
        &f.state.db,
        &f.admin,
        role_id,
        &service::role::PermAssign {
            permission_ids: vec![permission.id, delete_permission.id],
        },
    )
    .await
    .unwrap();
    assert_eq!(
        service::log::delete_ids(&f.state.db, &f.member, &[row.id])
            .await
            .unwrap(),
        1
    );
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_project_empty_success_content_refused_and_profile_identity() {
    let f = Fixture::new().await;
    let admin_profile =
        serde_json::to_value(service::auth::profile(&f.state.db, &f.admin).await.unwrap()).unwrap();
    let member_profile = serde_json::to_value(
        service::auth::profile(&f.state.db, &f.member)
            .await
            .unwrap(),
    )
    .unwrap();
    assert_eq!(admin_profile["user"]["isSystemAdmin"], true);
    assert_eq!(member_profile["user"]["isSystemAdmin"], false);
    let id = draft(&f).await;
    service::project::delete(&f.state, &f.admin, id)
        .await
        .unwrap();
    assert!(projects::Entity::find_by_id(id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
    assert!(matches!(
        service::project::delete(&f.state, &f.admin, id).await,
        Err(AppError::NotFound)
    ));
    let mut project: projects::ActiveModel = projects::Entity::find_by_id(f.project_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap()
        .into();
    project.status = Set(crate::entity::enums::ProjectStatus::Completed);
    project.update(&f.state.db).await.unwrap();
    assert!(matches!(
        service::project::delete(&f.state, &f.admin, f.project_id).await,
        Err(AppError::BadRequest(_))
    ));
    assert!(crate::entity::rounds::Entity::find_by_id(f.round_id)
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delete_safety_project_writes_recheck_invalidated_actor_after_lock_wait() {
    use crate::entity::{
        enums::{CommonStatus, ProjectStatus},
        roles, users,
    };
    for action in ["update", "status"] {
        let f = Fixture::new().await;
        let id = draft(&f).await;
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
        let txn = f.state.db.begin().await.unwrap();
        let row = projects::Entity::find_by_id(id)
            .lock_exclusive()
            .one(&txn)
            .await
            .unwrap()
            .unwrap();
        let supplier_id = row.supplier_id;
        let blocker: u64 = txn
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
        let user = f.member.clone();
        let pending = tokio::spawn(async move {
            if action == "update" {
                service::project::update(
                    &db,
                    &user,
                    id,
                    &service::project::ProjectUpsert {
                        name: "失效请求不应生效".into(),
                        description: None,
                        supplier_id,
                    },
                )
                .await
            } else {
                service::project::set_status(
                    &db,
                    &user,
                    id,
                    &service::project::StatusChange {
                        status: "IN_PROGRESS".into(),
                    },
                )
                .await
            }
        });
        tokio::time::timeout(std::time::Duration::from_secs(12), async {
            loop {
                assert!(!pending.is_finished());
                let wait = f.state.db.query_one(Statement::from_string(DbBackend::MySql, format!("SELECT COUNT(*) AS n FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX b ON b.trx_id=w.blocking_trx_id WHERE b.trx_mysql_thread_id={blocker}"))).await.unwrap().unwrap();
                if wait.try_get::<i64>("", "n").unwrap() > 0 { break; }
                tokio::time::sleep(std::time::Duration::from_millis(100)).await;
            }
        }).await.expect("writer must wait for project lock");
        let mut actor: users::ActiveModel = users::Entity::find_by_id(f.member.id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap()
            .into();
        actor.status = Set(CommonStatus::Disabled);
        actor.update(&f.state.db).await.unwrap();
        txn.commit().await.unwrap();
        let result = pending.await.unwrap();
        assert!(
            matches!(result, Err(AppError::Forbidden)),
            "{action} committed after actor invalidation: {result:?}"
        );
        let kept = projects::Entity::find_by_id(id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(kept.status, ProjectStatus::Draft);
        assert_eq!(kept.name, row.name);
    }
}
