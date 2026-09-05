//! 邮件 outbox worker：轮询 PENDING → SMTP 发送 → SENT / 退避重试 → 超限 FAILED。
//! 重试节奏：retry_count 0/1/2 → 立即重试（每 30s 轮询天然错开），≥3 置 FAILED 保留人工介入。
use chrono::{DateTime, Duration, Timelike, Utc};
use sea_orm::{
    ColumnTrait, Condition, DatabaseConnection, EntityTrait, QueryFilter, QueryOrder, QuerySelect,
    Set,
};

use crate::entity::email_outbox;
use crate::entity::enums::OutboxStatus;
use crate::state::AppState;

const MAX_RETRY: i32 = 3;

struct SendFailure {
    message: String,
    retryable: bool,
}

fn retry_delay_seconds(retry_count: i32) -> i64 {
    30 * 2_i64.pow(retry_count.saturating_sub(1).clamp(0, 6) as u32)
}

fn failure_is_terminal(retry_count_after_failure: i32, retryable: bool) -> bool {
    !retryable || retry_count_after_failure >= MAX_RETRY
}

fn recipient_mailbox(
    address: &str,
) -> Result<lettre::message::Mailbox, lettre::address::AddressError> {
    address.parse()
}

async fn claim_mail(
    db: &DatabaseConnection,
    mail: &email_outbox::Model,
    now: DateTime<Utc>,
) -> Result<Option<DateTime<Utc>>, crate::error::AppError> {
    let lease = (now + Duration::minutes(10)).with_nanosecond(0).unwrap();
    let claim = email_outbox::ActiveModel {
        status: Set(OutboxStatus::Sending),
        next_attempt_at: Set(Some(lease)),
        ..Default::default()
    };
    let claimed = email_outbox::Entity::update_many()
        .set(claim)
        .filter(email_outbox::Column::Id.eq(mail.id))
        .filter(email_outbox::Column::Status.eq(mail.status))
        .filter(email_outbox::Column::RetryCount.eq(mail.retry_count))
        .filter(match mail.next_attempt_at {
            Some(at) => email_outbox::Column::NextAttemptAt.eq(at),
            None => email_outbox::Column::NextAttemptAt.is_null(),
        })
        .filter(
            Condition::any()
                .add(email_outbox::Column::NextAttemptAt.is_null())
                .add(email_outbox::Column::NextAttemptAt.lte(now)),
        )
        .exec(db)
        .await?;
    Ok((claimed.rows_affected == 1).then_some(lease))
}

async fn finish_mail(
    db: &DatabaseConnection,
    am: email_outbox::ActiveModel,
    lease: DateTime<Utc>,
) -> Result<bool, crate::error::AppError> {
    let id = am.id.clone().unwrap();
    let result = email_outbox::Entity::update_many()
        .set(am)
        .filter(email_outbox::Column::Id.eq(id))
        .filter(email_outbox::Column::Status.eq(OutboxStatus::Sending))
        .filter(email_outbox::Column::NextAttemptAt.eq(lease))
        .exec(db)
        .await?;
    Ok(result.rows_affected == 1)
}

pub async fn run(state: AppState) {
    let mut ticker = tokio::time::interval(std::time::Duration::from_secs(30));
    // 定期清理每 10 分钟一轮（启动后先跑一轮）
    let gc_every = std::time::Duration::from_secs(600);
    let mut last_gc = std::time::Instant::now() - gc_every;
    loop {
        ticker.tick().await;
        // 过期上传会话清理顺带在此循环里做
        crate::service::upload::gc_expired(&state).await;
        if last_gc.elapsed() >= gc_every {
            last_gc = std::time::Instant::now();
            crate::service::gc::run_all(&state).await;
            if let Err(e) = crate::service::notify::enqueue_storage_warning(&state).await {
                tracing::warn!(error = ?e, "存储空间告警入队失败");
            }
        }
        if state.cfg.smtp.host.is_empty() {
            continue;
        }
        // 系统参数总开关关闭时暂停发信（不清空队列，恢复后继续）
        if !crate::service::notify::enabled(&state.db).await {
            continue;
        }
        if let Err(e) = flush_pending(&state).await {
            tracing::warn!(error = ?e, "邮件发送批次失败");
        }
    }
}

async fn flush_pending(state: &AppState) -> Result<(), crate::error::AppError> {
    use lettre::transport::smtp::authentication::Credentials;
    use lettre::{AsyncSmtpTransport, AsyncTransport, Message, Tokio1Executor};

    let db = &state.db;
    let now = Utc::now();
    let pending = email_outbox::Entity::find()
        .filter(
            Condition::any()
                .add(
                    Condition::all()
                        .add(email_outbox::Column::Status.eq(OutboxStatus::Pending))
                        .add(
                            Condition::any()
                                .add(email_outbox::Column::NextAttemptAt.is_null())
                                .add(email_outbox::Column::NextAttemptAt.lte(now)),
                        ),
                )
                .add(
                    Condition::all()
                        .add(email_outbox::Column::Status.eq(OutboxStatus::Sending))
                        .add(email_outbox::Column::NextAttemptAt.lte(now)),
                ),
        )
        .order_by_asc(email_outbox::Column::Id)
        .limit(10)
        .all(db)
        .await?;
    if pending.is_empty() {
        return Ok(());
    }

    let cfg = &state.cfg.smtp;
    let mailer = if cfg.port == 465 {
        AsyncSmtpTransport::<Tokio1Executor>::relay(&cfg.host)
            .map_err(|e| crate::error::AppError::Internal(e.to_string()))?
            .credentials(Credentials::new(cfg.username.clone(), cfg.password.clone()))
            .build()
    } else {
        AsyncSmtpTransport::<Tokio1Executor>::starttls_relay(&cfg.host)
            .map_err(|e| crate::error::AppError::Internal(e.to_string()))?
            .credentials(Credentials::new(cfg.username.clone(), cfg.password.clone()))
            .port(cfg.port)
            .build()
    };

    for mail in pending {
        // 原子认领；其他实例认领成功后本实例 rows_affected=0。SENDING 超时后允许重新认领。
        let Some(lease) = claim_mail(db, &mail, Utc::now()).await? else {
            continue;
        };
        // 地址解析失败按发送失败处理（记 last_error 走重试），不能 unwrap——panic 会杀掉 worker 协程
        let from: Result<lettre::message::Mailbox, _> =
            cfg.from.parse().or_else(|_| cfg.username.parse());
        let to = recipient_mailbox(&mail.recipient_email);
        let result: Result<(), SendFailure> = match (from, to) {
            (Ok(from), Ok(to)) => match Message::builder()
                .from(from)
                .to(to)
                .subject(&mail.subject)
                .body(mail.body.clone())
            {
                Ok(m) => mailer.send(m).await.map(|_| ()).map_err(|e| SendFailure {
                    retryable: !e.is_permanent(),
                    message: e.to_string(),
                }),
                Err(e) => Err(SendFailure {
                    message: e.to_string(),
                    retryable: false,
                }),
            },
            _ => Err(SendFailure {
                message: "发件/收件地址配置无效".to_string(),
                retryable: false,
            }),
        };
        let mut am: email_outbox::ActiveModel = mail.into();
        match result {
            Ok(()) => {
                am.status = Set(OutboxStatus::Sent);
                am.sent_at = Set(Some(chrono::Utc::now()));
                am.next_attempt_at = Set(None);
            }
            Err(e) => {
                let retries = am.retry_count.unwrap() + 1;
                let terminal = failure_is_terminal(retries, e.retryable);
                am.retry_count = Set(retries);
                am.last_error = Set(Some(e.message));
                am.status = Set(if terminal {
                    OutboxStatus::Failed
                } else {
                    OutboxStatus::Pending
                });
                am.next_attempt_at = Set(if terminal {
                    None
                } else {
                    Some(Utc::now() + Duration::seconds(retry_delay_seconds(retries)))
                });
            }
        }
        finish_mail(db, am, lease).await?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use super::{failure_is_terminal, retry_delay_seconds};
    use sea_orm::ActiveModelTrait;

    #[test]
    fn invalid_recipient_must_not_be_redirected_to_sender() {
        assert!(super::recipient_mailbox("invalid-address").is_err());
        assert_eq!(
            super::recipient_mailbox("recipient@example.invalid")
                .unwrap()
                .email
                .to_string(),
            "recipient@example.invalid"
        );
    }

    #[test]
    fn email_retry_uses_exponential_backoff() {
        assert_eq!(retry_delay_seconds(1), 30);
        assert_eq!(retry_delay_seconds(2), 60);
        assert_eq!(retry_delay_seconds(3), 120);
    }

    #[test]
    fn permanent_smtp_errors_fail_without_retrying() {
        assert!(failure_is_terminal(1, false));
        assert!(!failure_is_terminal(1, true));
        assert!(failure_is_terminal(3, true));
    }

    async fn leased_mail(
        f: &crate::regression::Fixture,
        lease: DateTime<Utc>,
    ) -> email_outbox::Model {
        email_outbox::ActiveModel {
            event_type: Set(crate::entity::enums::OutboxEventType::FileUploaded),
            project_id: Set(Some(f.project_id)),
            round_id: Set(Some(f.round_id)),
            recipient_user_id: Set(Some(f.member.id)),
            recipient_email: Set("regression@example.invalid".into()),
            subject: Set("lease regression".into()),
            body: Set("test".into()),
            status: Set(OutboxStatus::Sending),
            retry_count: Set(0),
            next_attempt_at: Set(Some(lease)),
            created_at: Set(Utc::now()),
            ..Default::default()
        }
        .insert(&f.state.db)
        .await
        .unwrap()
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn expired_sending_claim_is_exclusive() {
        let f = crate::regression::Fixture::new().await;
        let now = Utc::now().with_nanosecond(0).unwrap();
        let mail = leased_mail(&f, now - Duration::minutes(1)).await;
        let (a, b) = tokio::join!(
            claim_mail(&f.state.db, &mail, now),
            claim_mail(&f.state.db, &mail, now + Duration::seconds(1))
        );
        assert_eq!(
            usize::from(a.unwrap().is_some()) + usize::from(b.unwrap().is_some()),
            1,
            "同一过期 SENDING 只能被一个 worker 认领"
        );
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn stale_completion_cannot_replace_new_lease() {
        let f = crate::regression::Fixture::new().await;
        let old_lease = Utc::now().with_nanosecond(0).unwrap();
        let mail = leased_mail(&f, old_lease + Duration::minutes(10)).await;
        let mut result: email_outbox::ActiveModel = mail.into();
        result.status = Set(OutboxStatus::Sent);
        assert!(
            !finish_mail(&f.state.db, result, old_lease).await.unwrap(),
            "旧租约不得覆盖新认领者状态"
        );
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn stale_failure_cannot_reset_new_lease_or_retry_progress() {
        let f = crate::regression::Fixture::new().await;
        let old_lease = Utc::now().with_nanosecond(0).unwrap();
        let mail = leased_mail(&f, old_lease + Duration::minutes(10)).await;
        let mut result: email_outbox::ActiveModel = mail.clone().into();
        result.status = Set(OutboxStatus::Pending);
        result.retry_count = Set(2);
        result.last_error = Set(Some("stale transport failure".into()));
        result.next_attempt_at = Set(Some(old_lease + Duration::seconds(60)));
        assert!(!finish_mail(&f.state.db, result, old_lease).await.unwrap());
        let current = email_outbox::Entity::find_by_id(mail.id)
            .one(&f.state.db)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(current.status, OutboxStatus::Sending);
        assert_eq!(current.retry_count, mail.retry_count);
        assert_eq!(current.next_attempt_at, mail.next_attempt_at);
        assert_eq!(current.last_error, mail.last_error);
        let mut success: email_outbox::ActiveModel = current.into();
        success.status = Set(OutboxStatus::Sent);
        success.sent_at = Set(Some(Utc::now()));
        success.next_attempt_at = Set(None);
        assert!(
            finish_mail(&f.state.db, success, mail.next_attempt_at.unwrap())
                .await
                .unwrap()
        );
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn pending_claim_is_exclusive_and_respects_retry_deadline() {
        let f = crate::regression::Fixture::new().await;
        let now = Utc::now().with_nanosecond(0).unwrap();
        let mut active: email_outbox::ActiveModel =
            leased_mail(&f, now + Duration::seconds(30)).await.into();
        active.status = Set(OutboxStatus::Pending);
        active.retry_count = Set(1);
        let mail = active.update(&f.state.db).await.unwrap();
        assert!(claim_mail(&f.state.db, &mail, now).await.unwrap().is_none());
        let (a, b) = tokio::join!(
            claim_mail(&f.state.db, &mail, now + Duration::seconds(30)),
            claim_mail(&f.state.db, &mail, now + Duration::seconds(30))
        );
        assert_eq!(
            usize::from(a.unwrap().is_some()) + usize::from(b.unwrap().is_some()),
            1
        );
    }

    #[tokio::test]
    #[ignore = "isolated MySQL required"]
    async fn stale_scan_cannot_reset_retry_progress() {
        let f = crate::regression::Fixture::new().await;
        let now = Utc::now().with_nanosecond(0).unwrap();
        let mut active: email_outbox::ActiveModel = leased_mail(&f, now).await.into();
        active.status = Set(OutboxStatus::Pending);
        active.next_attempt_at = Set(None);
        let stale = active.update(&f.state.db).await.unwrap();
        let mut active: email_outbox::ActiveModel = stale.clone().into();
        active.retry_count = Set(1);
        active.next_attempt_at = Set(Some(now + Duration::seconds(30)));
        active.update(&f.state.db).await.unwrap();
        assert!(
            claim_mail(&f.state.db, &stale, now + Duration::seconds(31))
                .await
                .unwrap()
                .is_none(),
            "过期扫描不得使用旧重试次数再次发送"
        );
    }
}
