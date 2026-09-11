//! Delegated managers must not cross their own authority boundary.
use crate::{
    entity::{permissions, roles, users},
    error::AppError,
    regression::Fixture,
    service,
};
use sea_orm::{ColumnTrait, EntityTrait, QueryFilter};

async fn delegated(f: &Fixture, code: &str) -> (u64, u64) {
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
            name: format!("委派回归-{}", f.member.id),
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
    (role, permission.id)
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delegation_user_manager_cannot_promote_or_reset_admin() {
    let f = Fixture::new().await;
    delegated(&f, "user:manage").await;
    let admin_role = roles::Entity::find()
        .filter(roles::Column::IsBuiltIn.eq(true))
        .filter(roles::Column::Name.eq("系统管理员"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    let before = users::Entity::find_by_id(f.admin.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert!(matches!(
        service::user::assign_roles(
            &f.state.db,
            &f.member,
            f.member.id,
            &service::user::RoleAssign {
                role_ids: vec![admin_role.id]
            }
        )
        .await,
        Err(AppError::Forbidden)
    ));
    assert!(matches!(
        service::user::reset_password(
            &f.state.db,
            &f.member,
            f.admin.id,
            &service::user::PasswordReset {
                new_password: "Regression456".into()
            }
        )
        .await,
        Err(AppError::Forbidden)
    ));
    let after = users::Entity::find_by_id(f.admin.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(before.password_hash, after.password_hash);
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn delegation_role_manager_cannot_add_unowned_permissions() {
    let f = Fixture::new().await;
    let (role, own) = delegated(&f, "role:manage").await;
    let other = permissions::Entity::find()
        .filter(permissions::Column::Code.eq("user:manage"))
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert!(matches!(
        service::role::assign_permissions(
            &f.state.db,
            &f.member,
            role,
            &service::role::PermAssign {
                permission_ids: vec![own, other.id]
            }
        )
        .await,
        Err(AppError::Forbidden)
    ));
    // Legitimate edits within the delegated permission ceiling remain possible.
    service::role::assign_permissions(
        &f.state.db,
        &f.member,
        role,
        &service::role::PermAssign {
            permission_ids: vec![own],
        },
    )
    .await
    .unwrap();
    assert!(!service::perm::permission_codes(&f.state.db, f.member.id)
        .await
        .unwrap()
        .contains(&"user:manage".into()));
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn token_gc_preserves_rotated_hashes_until_expiry() {
    use crate::entity::refresh_tokens;
    use sea_orm::{ActiveModelTrait, Set};
    let f = Fixture::new().await;
    let mut ids = Vec::new();
    for days in [20, -8] {
        let row = refresh_tokens::ActiveModel {
            user_id: Set(f.member.id),
            session_id: Set(uuid::Uuid::new_v4().to_string()),
            token_hash: Set(uuid::Uuid::new_v4().simple().to_string()),
            revoked: Set(true),
            expires_at: Set(chrono::Utc::now() + chrono::Duration::days(days)),
            created_at: Set(chrono::Utc::now() - chrono::Duration::days(10)),
            ip: Set(None),
            ..Default::default()
        }
        .insert(&f.state.db)
        .await
        .unwrap();
        ids.push(row.id);
    }
    service::gc::run_all(&f.state).await;
    assert!(refresh_tokens::Entity::find_by_id(ids[0])
        .one(&f.state.db)
        .await
        .unwrap()
        .is_some());
    assert!(refresh_tokens::Entity::find_by_id(ids[1])
        .one(&f.state.db)
        .await
        .unwrap()
        .is_none());
}

#[tokio::test]
#[ignore = "isolated MySQL required"]
async fn auth_session_migration_can_replay_completed_up_without_rewriting_sid() {
    use crate::entity::refresh_tokens;
    use migration::{Migrator, MigratorTrait, SchemaManager};
    use sea_orm::{ActiveModelTrait, ConnectionTrait, Set};

    let f = Fixture::new().await;
    let stable_sid = uuid::Uuid::new_v4().to_string();
    let row = refresh_tokens::ActiveModel {
        user_id: Set(f.member.id),
        session_id: Set(stable_sid.clone()),
        token_hash: Set(uuid::Uuid::new_v4().simple().to_string()),
        revoked: Set(false),
        expires_at: Set(chrono::Utc::now() + chrono::Duration::days(20)),
        created_at: Set(chrono::Utc::now()),
        ip: Set(None),
        ..Default::default()
    }
    .insert(&f.state.db)
    .await
    .unwrap();

    let migration = Migrator::migrations()
        .into_iter()
        .find(|item| item.name() == "m20260911_000017_auth_session_families")
        .expect("auth session migration must remain registered");
    assert_eq!(migration.name(), "m20260911_000017_auth_session_families");
    let manager = SchemaManager::new(&f.state.db);

    migration.up(&manager).await.unwrap();
    let after_first = refresh_tokens::Entity::find_by_id(row.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(after_first.session_id, stable_sid);

    migration.up(&manager).await.unwrap();
    let after_second = refresh_tokens::Entity::find_by_id(row.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(after_second.session_id, after_first.session_id);

    // Simulate DDL interruption after ADD COLUMN but before backfill/NOT NULL/index.
    f.state
        .db
        .execute_unprepared("DROP INDEX idx_refresh_tokens_session_state ON refresh_tokens")
        .await
        .unwrap();
    f.state
        .db
        .execute_unprepared("ALTER TABLE refresh_tokens MODIFY COLUMN session_id VARCHAR(36) NULL")
        .await
        .unwrap();
    f.state
        .db
        .execute_unprepared(&format!(
            "UPDATE refresh_tokens SET session_id = NULL WHERE id = {}",
            row.id
        ))
        .await
        .unwrap();
    migration.up(&manager).await.unwrap();
    let recovered = refresh_tokens::Entity::find_by_id(row.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(recovered.session_id, format!("{:0>36x}", row.id));
    assert!(manager
        .has_index("refresh_tokens", "idx_refresh_tokens_session_state")
        .await
        .unwrap());
    migration.up(&manager).await.unwrap();
    let replayed = refresh_tokens::Entity::find_by_id(row.id)
        .one(&f.state.db)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(replayed.session_id, recovered.session_id);
}
