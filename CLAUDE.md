# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout — read this first

- `server_dotnet/` — **the only backend.** ASP.NET Core 10 (Minimal API) + EF Core 9/Pomelo + MySQL. All new features, bug fixes, and DB migrations go here. An earlier Rust + Axum + SeaORM prototype (`yf_server/`) existed during the initial build-out but has been removed from the repo; its history is still visible via `git log` if needed for archaeology, but nothing in `server_dotnet/` depends on it. Development uses the current EF-managed schema and does not support legacy schema/data compatibility.
- `web/` — React + TypeScript + Vite frontend, talks to `/api/v1`.
- `third_party/vue-office-source-2024-12-30/` — vendored reference source for the Excel/PPTX preview runtime; not part of the build graph directly (see `web/scripts/build-excel-preview.mjs` / `build-pptx-preview.mjs`).
- `server_dotnet/docs/*.md` — dated contract documents (e.g. `主项目与子项目协作契约-2026-09-16.md`) are the source of truth for business rules like project/subproject workflow, metadata dictionaries, copy semantics, and email notification policy. Check the newest-dated doc for a topic before assuming behavior from code alone.

Root-level dated `*.md` files (验收记录, 检查与修复记录, 需求文档, etc.) are point-in-time audit/acceptance records, not living docs — useful for history, not for current behavior.

## Backend (server_dotnet) — commands

Run from `server_dotnet/`. Requires .NET 10 SDK (`global.json` pins `10.0.300`) and MySQL 5.7/8.

```powershell
# Local run (config must live outside the IIS site dir; never commit secrets)
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Local.json'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8080'
dotnet run --project .\Yf.Api

# One-time DB init (new empty DB only; prompts for bootstrap admin password)
$secret = Read-Host '初始管理员密码' -AsSecureString
$env:YF_BOOTSTRAP_PASSWORD = (New-Object System.Net.NetworkCredential('', $secret)).Password
dotnet run --project .\Yf.Api -- --initialize-database

# Explicit schema migration for an existing DB (backup first)
dotnet run --project .\Yf.Api -- --migrate-database

# Unit/integration tests (xUnit v3)
dotnet test --project .\tests\Yf.Api.Tests.csproj
# Run a single test class/method
dotnet test --project .\tests\Yf.Api.Tests.csproj --filter "FullyQualifiedName~ProjectsWorkflowTests"

# Full HTTP contract tests (Python 3.11+, pymysql) — needs a throwaway local MySQL admin connection
dotnet build .\TestHost\Yf.Api.TestHost.csproj
python .\scripts\test-isolated.py
python .\scripts\test-isolated.py --files-only   # files-only subset

# Maintenance (backup/restore/upgrade) tests
python .\scripts\test-maintenance.py

# Browser end-to-end (builds web + TestHost first)
dotnet build .\TestHost\Yf.Api.TestHost.csproj --no-restore
Push-Location ..\web; npm run build; Pop-Location
python .\scripts\test-browser.py --output ..\.runlogs\browser-NEW
```

Key env vars: `YF_CONFIG_PATH` (external appsettings for local/prod), `YF_BOOTSTRAP_PASSWORD` (init only, unset after use), `YF_TEST_DATABASE_URL` (test suites only — `mysql://user:urlencoded_pass@127.0.0.1:port/ignored`, must be localhost/127.0.0.1/::1 for maintenance tests). Config precedence: `appsettings.json` → environment config → `appsettings.Local.json` → `YF_CONFIG_PATH` file → env vars → CLI args. Production IIS only honors `YF_CONFIG_PATH` and rejects `App__*`/`App:*` overrides.

IIS packaging/maintenance: `scripts/publish-iis.ps1` (build+package), `deploy/install-iis.ps1` (first install on a new machine), `maintain-iis.ps1` in the published package (Backup/Upgrade/Restore of an existing site — never authored by hand, ships inside the release zip).

## Frontend (web) — commands

Run from `web/`. Requires Node 22.13+/24+.

```powershell
npm ci
npm run dev      # vite dev server on 127.0.0.1:5173, proxies /api -> 127.0.0.1:8080
npm run build    # tsc -b && vite build (predev/prebuild also (re)build the Excel/PPTX preview bundles)
npm run lint     # oxlint
npm test         # node --test test/*.test.cjs
```

`npm run dev`/`build` trigger `predev`/`prebuild` hooks that regenerate `.excel-preview-build/` and `.pptx-preview-build/` via `scripts/build-excel-preview.mjs` / `build-pptx-preview.mjs` — don't hand-edit those generated dirs.

The dev proxy points at port 8080 (`server_dotnet`).

## Backend architecture (server_dotnet)

Modules under `Yf.Api/Modules/` are the unit of organization; each owns its own models, service(s), and a `*Module.cs` that wires Minimal API routes:

- **Identity** — employee-ID/password login, JWT issuance, refresh-session rotation/replay revocation, permission checks (`PermissionService`), login rate limiting (10 failures → 15 min lock, tracked in DB across instances).
- **Admin** — organizations (fixed 3-level hierarchy), internal accounts, roles/permissions, suppliers, supplier accounts. Delegation-limit and last-admin protections live here.
- **Projects** — the core domain. Current model is **main project + subproject**: the main project (`ProjectGroup*`) holds supplier, work-order number, machine/robot info, responsible user, section, priority, due date; files/messages/activity/acceptance all belong to the *subproject*. Read `docs/主项目与子项目协作契约-2026-09-16.md` before changing workflow, submit/confirm/reject/withdraw, or ownership-transfer logic — it documents `expectedSubmissionId` optimistic-concurrency requirements and freeze rules for completed subprojects.
- **Files** — chunked upload/resume, merge+checksum, download, in-browser preview (PDF/XLS/XLSX/PPTX/images via `GET /files/{id}/content`, 50 MiB cap) and video streaming (MP4/WebM/OGV via short-lived media-session cookie + Range requests on `/files/{id}/media`), batch ZIP, soft delete + GC.
- **System** — system parameters, audit log, mail outbox + background TLS SMTP sender, email notification policy toggles (`notify.enabled`, per-audience, per-event-type — see `docs/邮件提醒规则契约-2026-09-16.md`).
- **Infrastructure** — cross-cutting: `AppDb`/`YfDbContext` (EF Core and MySqlConnector connection/transaction interop), `EfDatabaseLifecycle` (explicit EF migration initialization and current-schema validation), `AuditService`, `MySqlNamedLock` (migration locking), `DatabaseTransportPolicy` (enforces `SslMode=VerifyFull` for non-loopback MySQL hosts). Raw SQL is limited to database-clock reads, row/advisory locks, and other documented atomic boundaries; ordinary business queries and writes use EF Core. Test-only fixtures may still use Dapper.

Cross-cutting rules worth knowing before editing:
- Authorization is enforced both at the route/middleware layer and **re-checked inside write transactions** (defends against concurrent permission revocation mid-request).
- Deletion is gated by dedicated permission points per entity (`project:delete`, `file:delete`, `supplier:delete`, etc.) plus entity-specific retention rules (e.g. a project can only be deleted while draft/terminated and with no files/messages/uploads) — see the "删除操作" table in `server_dotnet/README.md`.
- Supplier accounts share their project scope at the supplier-company level (all enabled accounts under one supplier see the same projects); there is no per-user project-membership model anymore (`project_members` was removed in schema v10).
- Only supplier accounts can submit (`project:submit`); only internal accounts can confirm/reject (`project:confirm`); acceptance is always handled internally.
- Passwords: Argon2 PHC, 6–20 Unicode chars, common-weak-password rejection; legacy passwords >20 chars still verify without forced reset.
- Database schema is versioned (`yf_schema_migrations`); each schema version bump (v5..v11 so far) is documented inline in `server_dotnet/README.md` — check it for what changed before writing code that assumes an older shape.

## Frontend architecture (web)

- `src/api/client.ts` — the HTTP client (axios) talking to `/api/v1`; `src/api/types.ts` mirrors backend contracts (kept in sync with `tests/Contracts/api-v1.json` on the backend).
- `src/store/` — Zustand stores: `auth.ts` (session/user), `collaboration.ts` (live project/message state).
- `src/services/projectRealtime.ts` — SignalR client for pushed collaboration updates (new messages, activity, status changes).
- `src/pages/project/` — main project/subproject list & detail screens; this is the largest and most actively changed area (see recent git history: watermarking, notification policy UI, collaboration contract changes).
- `src/pages/{org,rbac,supplier,system}/` — admin-side screens (organizations, roles/permissions, suppliers, system params/mail settings).
- PDF preview is custom: local PDF.js 6.3.289, page-by-page rendering, assets emitted by the `pdfAssets()` Vite plugin (`pdf-assets.ts`) into `dist/pdfjs/<version>/`. Excel/PPTX preview bundles are separately built (see build:excel-preview / build:pptx-preview scripts) from vendored `pptx-preview`/`exceljs`/`xlsx` (sheetjs CDN tarball) sources — don't assume standard npm resolution for `xlsx`.
- Document previews are watermarked (recent feature area — see `web/src/pages/project/ProjectDetail.css` and related components for density/spacing tuning).

## Testing philosophy in this repo

Tests here are deliberately end-to-end and isolation-heavy, not just unit tests:
- Every DB-touching test suite requires an explicit, separate test MySQL connection (`YF_TEST_DATABASE_URL`); nothing runs against the dev/business database, and a bare "exit code 0" does not prove a full pass — check failed/skipped/not-run counts are all zero.
- Browser tests build real artifacts (web `dist` + `TestHost`) and verify SHA-256 of what's actually under test, rejecting stale builds.
- `tests/Contracts/api-v1.json` is the authoritative frontend↔backend HTTP contract; update it alongside any route/method change.
