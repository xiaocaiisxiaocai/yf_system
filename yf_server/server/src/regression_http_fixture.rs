//! Only linked into the test binary. No fixture routes exist in the application binary.
use axum::{extract::State, routing::get, Json, Router};
use migration::MigratorTrait;

async fn challenge(State(state): State<crate::state::AppState>) -> Json<serde_json::Value> {
    let (id, _) = crate::service::captcha::issue(&state.captchas);
    let code = state.captchas.lock().unwrap()[&id].0.clone();
    Json(serde_json::json!({"captchaId":id,"captchaCode":code}))
}

#[tokio::test]
#[ignore = "private loopback HTTP fixture; started/stopped by test-http-isolated.py"]
async fn serve_http_fixture() {
    let _ = tracing_subscriber::fmt()
        .with_env_filter("info,sea_orm=warn,sqlx=warn")
        .try_init();
    let cfg = crate::config::Config::load().unwrap();
    let test_url = std::env::var("YF_TEST_DATABASE_URL").expect("isolated fixture only");
    assert_eq!(cfg.database.url, test_url);
    assert!(test_url
        .rsplit('/')
        .next()
        .unwrap()
        .starts_with("yf_test_http_"));
    let addr: std::net::SocketAddr = cfg.server.addr.parse().unwrap();
    assert!(addr.ip().is_loopback());
    let db = sea_orm::Database::connect(&test_url).await.unwrap();
    migration::Migrator::up(&db, None).await.unwrap();
    let state = crate::state::AppState::new(db, cfg);
    let fixture = Router::new()
        .route("/__test/captcha", get(challenge))
        .with_state(state.clone());
    let app = crate::handler::router(state).merge(fixture);
    let listener = tokio::net::TcpListener::bind(addr).await.unwrap();
    axum::serve(
        listener,
        app.into_make_service_with_connect_info::<std::net::SocketAddr>(),
    )
    .await
    .unwrap();
}
