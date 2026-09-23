# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout — read this first

- `server_dotnet/` — **the only backend.** ASP.NET Core 8 (Minimal API) + EF Core 9/Pomelo + MySQL. All new features, bug fixes, and DB migrations go here. An earlier Rust + Axum + SeaORM prototype (`yf_server/`) existed during the initial build-out but has been removed from the repo; its history is still visible via `git log` if needed for archaeology, but nothing in `server_dotnet/` depends on it. Development uses the current EF-managed schema and does not support legacy schema/data compatibility.
- `web/` — React 18 + TypeScript 6 + Vite 8 (rolldown) + Arco Design frontend, talks to `/api/v1`. In production the ASP.NET app serves the built SPA from `wwwroot` on the same site.
- `web/vendor/vue-office-{excel,pptx,pdf}/` — the preview source that is **actually built** (see Frontend architecture). `third_party/vue-office-source-2024-12-30/` is a git-ignored, locally unpacked upstream reference copy. The PPTX template the browser tests need (`scripts/browser/preview-extras.cjs`) is a trimmed, committed copy at `server_dotnet/scripts/browser/fixtures/pptx-template.zip`.
- `server_dotnet/docs/*.md` — dated contract documents (e.g. `主项目与子项目协作契约-2026-09-16.md`) are the source of truth for business rules like project/subproject workflow, metadata dictionaries, copy semantics, and email notification policy. Check the newest-dated doc for a topic before assuming behavior from code alone.
- OEM platform: **paused and removed** from backend, frontend and release packages. Migrations `AddOemPlatform` / `DropOemPlatform` stay in history on purpose (covered by `SchemaShapeTests`) — don't delete or squash them, and don't reintroduce OEM code without re-reading the root `README.md` note.

`docs/history/` contains the former root-level dated audit/acceptance records. They describe historical behavior and may cite removed scratch paths; use current source and contracts for current behavior. The collaboration requirements are in `docs/公司与供应商协作平台-需求文档.md`; dev-data init/reset is in `docs/开发数据初始化说明.md`.

IIS deployment folders belong under repository-root `deloy/` (sic); archive sidecars are opt-in via `-CreateArchive`. Other development script outputs belong under `.artifacts/`: `cache/` for downloads, `tests/` for test evidence and scratch, `reports/` for retained verification evidence, and `backups/` for private historical backups. Do not generate local release packages in `D:\Releases`. Standard frontend build paths and .NET `bin/obj` stay in their existing project-local locations. Target-server installation/storage paths remain governed by the deployment contract.

## Backend (server_dotnet) — commands

Run from `server_dotnet/`. Requires .NET 8 SDK (`global.json` selects `8.0.412`) and MySQL 5.7/8.

```powershell
# Local run (config must live outside the IIS site dir; never commit secrets)
# (template: deploy/appsettings.example.json; local HTTP needs CookieSecure=false)
$env:YF_CONFIG_PATH = 'D:\YfConfig\appsettings.Local.json'
dotnet run --project .\Yf.Api --launch-profile Yf.Local   # Development env, http://127.0.0.1:8080

# Pre-start dependency check (config, EF migrations, storage R/W); JSON output, non-zero on failure.
# readyForStartup=true does not prove the API is listening — also hit /health and a real login.
dotnet restore .\Yf.Api\Yf.Api.csproj --locked-mode
powershell -NoProfile -File .\scripts\check-dev.ps1 -ConfigPath 'D:\YfConfig\appsettings.Local.json'

# One-time DB init (new empty DB only; creates only the `admin` account + 系统管理员 role)
$secret = Read-Host '初始管理员密码' -AsSecureString
$env:YF_BOOTSTRAP_PASSWORD = (New-Object System.Net.NetworkCredential('', $secret)).Password
dotnet run --project .\Yf.Api -- --initialize-database

# Explicit schema migration for an existing DB (backup first)
dotnet run --project .\Yf.Api -- --migrate-database

# Other one-shot modes (mutually exclusive): --check-development-readiness, --inspect-development-data,
# --reset-development-data --confirm-database <db> --confirm-storage-root <path>  (loopback only; see docs/开发数据初始化说明.md)

# New EF migration: dotnet-ef is pinned to 9.0.20 (= EF packages) in .config/dotnet-tools.json.
# Needs YF_EF_DESIGN_CONNECTION pointing at a DISPOSABLE design-time DB (never a business DB).
dotnet tool restore
$env:YF_EF_DESIGN_CONNECTION = 'Server=127.0.0.1;Port=3306;Database=yf_ef_design;User=...;Password=...'
dotnet ef migrations add <Name> --project .\Yf.Api
dotnet ef migrations has-pending-model-changes --project .\Yf.Api   # sanity check; doesn't connect

# Unit/integration tests (xUnit v3 via VSTest). NOTE: `dotnet test --project` is .NET 10-only syntax
# and fails on SDK 8 with MSB1001 — pass the csproj positionally.
dotnet test .\tests\Yf.Api.Tests.csproj
dotnet test .\tests\Yf.Api.Tests.csproj --filter "FullyQualifiedName~ProjectsWorkflowTests"

# Full HTTP contract tests (Python 3.11+, pymysql) — needs a throwaway local MySQL admin connection
dotnet build .\TestHost\Yf.Api.TestHost.csproj
python .\scripts\test-isolated.py
python .\scripts\test-isolated.py --files-only   # files-only subset

# Script self-tests (no DB needed); run from scripts/
python -m unittest test_script_safety test_publish_defaults test_browser_step_evidence test_test_host_artifacts test_maintenance_optimization_guard

# Maintenance (backup/restore/upgrade) tests
python .\scripts\test-maintenance.py

# Browser end-to-end (builds web + TestHost first)
dotnet build .\TestHost\Yf.Api.TestHost.csproj --no-restore
Push-Location ..\web; npm run build; Pop-Location
$env:YF_PLAYWRIGHT_RUNNER = '<path>\playwright-skill\run.js'   # uses installed Chrome; installs nothing
python .\scripts\test-browser.py --output ..\.artifacts\tests\browser\browser-NEW   # fresh empty dir each run
python .\scripts\test-browser.py --steps auth fixtures system --output ...           # step subset

# Verify a published release folder (or -CreateArchive ZIP); report -> .artifacts/reports/releases
python .\scripts\verify-release.py ..\deloy\<version-dir>
```

Python test layout: the `scripts/test_*.py` files are mostly contract *modules* imported and orchestrated by `test-isolated.py` (not pytest suites); only the self-tests listed above are standalone `unittest`. Never run them with `python -O`/`PYTHONOPTIMIZE` (they rely on `assert`). HTTP/browser runners check that TestHost bytes match the API build — after rebuilding the API, rebuild TestHost too. `test-maintenance.py` also needs `mysql.exe`/`mysqldump.exe` on PATH. Browser `--steps` have ordering constraints (`business`/`*-edges` after `users`; `final`/`layout` after `business`); `--continue-on-failure` collects failures but still exits non-zero. Browser automation on this Windows machine has a known account-lockout risk (global rule CORE-19) — don't blindly re-run old acceptance scripts by relaunching the browser.

Key env vars: `YF_CONFIG_PATH` (absolute path to external appsettings for local/prod), `YF_BOOTSTRAP_PASSWORD` (init only, unset after use), `YF_EF_DESIGN_CONNECTION` (design-time `dotnet ef` only), `YF_TEST_DATABASE_URL` (test suites only — `mysql://user:urlencoded_pass@127.0.0.1:port/ignored`, must be localhost/127.0.0.1/::1 for maintenance tests). Config precedence: `appsettings.json` → environment config → `appsettings.Local.json` → `YF_CONFIG_PATH` file → env vars → CLI args. Production IIS only honors `YF_CONFIG_PATH` and rejects `App__*`/`App:*` overrides.

IIS packaging/maintenance: `scripts/publish-iis.ps1` (build+package into a unique folder under repo-root `deloy/`; ZIP only with `-CreateArchive`; writes `appsettings.Production.json` — including DB/JWT credentials and `App.BootstrapPassword` for first-start auto-init — from the git-ignored `deploy/publish-defaults.local.json`, which must have a real `WebBaseUrl`; treat the output as a private artifact), `deploy/install-iis.ps1` (first install on a new machine), `deploy/maintain-iis.ps1` (Backup/Upgrade/Restore of an existing site; copied into the package). See `deploy/README.md`.

## Frontend (web) — commands

Run from `web/`. Requires Node 22.13+/24+.

```powershell
npm ci
npm run dev      # vite dev server on 127.0.0.1:5173, proxies /api (incl. WebSocket) -> 127.0.0.1:8080
npm run build    # tsc -b && vite build (predev/prebuild also (re)build the Excel/PPTX preview bundles)
npm run lint     # oxlint
npm test         # node --test test/*.test.cjs
node --test test/pdf-preview.test.cjs   # single test file
```

`npm run dev`/`build` trigger `predev`/`prebuild` hooks that regenerate `.excel-preview-build/`, `.pptx-preview-build/` and `generated/*-viewer.html` — don't hand-edit those. `generated/` is imported by source (`?raw`), so a bare `tsc -b` fails until the preview build has run once. `test/browser-*.acceptance.txt` are stale historical snippets, not tests — port any scenario you need into `server_dotnet/scripts/browser/*.cjs`.

The dev proxy points at port 8080 (`server_dotnet`). The backend README runs the dev server as `npm run dev -- --host 127.0.0.1 --port 5180 --strictPort` to match `WebBaseUrl=http://127.0.0.1:5180` in the local config (CORS and the WebSocket origin allow-list both derive from `WebBaseUrl`). Never run two backends against the same business DB and storage.

## Backend architecture (server_dotnet)

Modules under `Yf.Api/Modules/` are the unit of organization; each owns its own models, service(s), and a `*Module.cs` exposing `Add*Module()` (DI) and `Map*Module()` (routes). Both are chained explicitly in `Infrastructure/ApiApplication.cs` — a new module must be added to both chains.

- **Identity** — employee-ID/password login, JWT issuance, refresh-session rotation/replay revocation, permission checks (`PermissionService`), login throttling: DB-tracked lockout (10 failures → 15 min, works across instances) plus an in-memory per-IP / per-IP+account rate limiter (`LoginRateLimiter`).
- **Admin** — organizations (fixed 3-level hierarchy 事业部 > 部门 > 课别), internal accounts, roles/permissions, suppliers, supplier accounts. Delegation limit = an actor can only grant permissions they own (`PermissionService.EnsureGrantableAsync`); supplier roles are restricted to the fixed `RoleService.SupplierPermissionCodes` set; last-active-admin protection in `UserService`.
- **Projects** — the core domain. Current model is **main project + subproject**: the main project (`ProjectGroup*`) holds supplier, work-order number, machine/robot info, responsible user, section, priority, due date; files/messages/activity/acceptance all belong to the *subproject*. Read `server_dotnet/docs/主项目与子项目协作契约-2026-09-16.md` before changing workflow, submit/confirm/reject/withdraw, or ownership-transfer logic — it documents `expectedSubmissionId` optimistic-concurrency requirements and freeze rules for completed subprojects. Realtime: SignalR hub at `/api/v1/collaboration/live` (`Realtime.cs`); services call `IProjectRealtimePublisher.PublishAsync(projectId, kind)` *after commit*, which pushes only a coalesced change-kind signal — clients refetch over REST, so never put data payloads or authorization-sensitive content in pushes.
- **Files** — chunked upload/resume, merge+checksum, download, in-browser preview (PDF/XLS/XLSX/PPTX/images via `GET /files/{id}/content`, 50 MiB cap) and video streaming (MP4/WebM/OGV via `POST /files/{id}/media-session` short-lived cookie + Range requests on `/files/{id}/media`), batch ZIP, soft delete + GC (`FilesMaintenanceService` hosted service).
- **System** — system parameters, audit log, mail outbox + background TLS SMTP sender (`MailWorker`), email notification policy toggles (`notify.enabled`, per-audience, per-event-type — see `server_dotnet/docs/邮件提醒规则契约-2026-09-16.md`). Notifications are enqueued into the outbox inside the business transaction; tests assert on the outbox, never real SMTP.
- **Infrastructure** — cross-cutting: `AppDb`/`YfDbContext` (EF Core and MySqlConnector connection/transaction interop), `EfDatabaseLifecycle` (explicit EF migration initialization and current-schema validation), `AuditService`, `MySqlNamedLock` (migration locking), `DatabaseTransportPolicy` (enforces `SslMode=VerifyFull` for non-loopback MySQL hosts). Raw SQL is limited to database-clock reads, row/advisory locks, and other documented atomic boundaries; ordinary business queries and writes use EF Core. Test-only fixtures may still use Dapper. `SchemaMigrations`/`SchemaBootstrap` are thin facades kept only for test callers.

Request/write pattern (copy it for new endpoints):
1. Route lambda: `await using var conn = await db.OpenAsync(ct);` then call the service with `AccessService.GetCurrent(context)` and `Ip(context)`.
2. Service: `AppDb.BeginTransactionAsync(conn)` → `AccessService.LockActorAsync` (shared lock on the `security.management_lock` gate row + re-read the actor's status/supplier) or `LockManagementAsync` (exclusive, for admin/permission-changing writes) → `AccessService.RequirePermissionAsync(conn, tx, current, "x:y")` → `EfDb.Use(conn, tx)` for EF work → `audit.WriteAsync(conn, tx, ...)` → commit → realtime publish.
3. Errors: throw `ApiException.BadRequest/Forbidden/...` (user-facing Chinese message); `ApiErrorMiddleware` renders `{ code, message }`. Unknown `/api/*` returns JSON 404 (code 40401), everything else falls back to the SPA.
4. `/api` request bodies are capped at 2 MiB except chunk `PUT .../chunks/...` and message-image uploads — a new large-body endpoint must be exempted in `ApiApplication.cs`.

Cross-cutting rules worth knowing before editing:
- Authorization is enforced both at the route/middleware layer and **re-checked inside write transactions** (the pattern above; defends against concurrent permission revocation mid-request).
- Deletion is gated by dedicated permission points per entity (`project:delete`, `file:delete`, `supplier:delete`, etc.) plus entity-specific retention rules (e.g. a project can only be deleted while draft/terminated and with no files/messages/uploads) — see the "删除操作" table in `server_dotnet/README.md`.
- Supplier accounts share their project scope at the supplier-company level (all enabled accounts under one supplier see the same projects); there is no per-user project-membership model anymore (no `project_members` table).
- Only supplier accounts can submit (`project:submit`); only internal accounts can confirm/reject (`project:confirm`); acceptance is always handled internally.
- Passwords: Argon2 PHC, 6–20 Unicode chars (≤256 UTF-8 bytes), common-weak-password rejection; legacy passwords >20 chars still verify without forced reset. The frontend rule mirror is `web/src/utils/password-policy.json` — keep it in sync with `PasswordService`.
- Schema authority is `Yf.Api/Infrastructure/Migrations` + `YfDbContextModelSnapshot` only. Change the model, then `dotnet ef migrations add <Name>` and review the diff; never use `EnsureCreated`, hand-written DDL, or edit `__EFMigrationsHistory`. On startup a non-empty DB must have an `__EFMigrationsHistory` that exactly matches the assembly's migrations (no auto-upgrade); a DB with missing/unknown history is rejected — recreate the empty dev DB rather than patching it.

## Frontend architecture (web)

- `src/api/client.ts` — the HTTP client (axios) talking to `/api/v1`; `src/api/types.ts` builds on `src/api/generated/api-types.ts`, generated from the backend OpenAPI contract (see Testing philosophy).
- `src/store/` — Zustand stores: `auth.ts` (session/user), `collaboration.ts` (live project/message state).
- `src/services/projectRealtime.ts` — SignalR client; on a pushed change-kind it refetches the affected data.
- `src/pages/project/` — main project/subproject list & detail screens; the largest and most actively changed area.
- `src/pages/{org,rbac,supplier,system}/` — admin-side screens (organizations, roles/permissions, suppliers, system params/mail settings).
- `src/components/` — shared collaboration UI: `FileTable.tsx` (file list + preview dialog), `ChunkUploader.tsx`, `MessagePanel.tsx`, and the `*Preview.tsx` viewers.
- Previews: PDF uses local PDF.js 6.3.289 (`PdfPreview.tsx`/`pdfEngine.ts`), page-by-page, assets emitted by the `pdfAssets()` Vite plugin (`pdf-assets.ts`) into `dist/pdfjs/<version>/`. Excel/PPTX run in a sandboxed iframe: `scripts/build-{excel,pptx}-preview.mjs` bundle `web/vendor/vue-office-{excel,pptx}/viewer.js` (with npm `pptx-preview`/`exceljs`/`xlsx`) as an IIFE and inline it into `generated/{excel,pptx}-viewer.html`, which `ExcelPreview.tsx`/`PptxPreview.tsx` import `?raw`. `xlsx` comes from the SheetJS CDN tarball, not the npm registry.
- CSP: `web/csp.ts` (Vite plugin, build only) injects a `<meta>` Content-Security-Policy into `dist/index.html` — no `unsafe-eval`/general `unsafe-inline` script. The Excel/PPTX srcdoc iframes inherit it, so each generated viewer must keep **exactly one** inline script, allowed by SHA-256 hash (the build fails otherwise). Browser tests' `waitForFunction` is patched in `scripts/browser/ui-lib.cjs` to poll via `page.evaluate`, because Playwright's native one needs eval.
- Arco styles: `main.tsx` imports `src/styles/arco-components.ts` (generated by `node scripts/generate-arco-styles.mjs`), not the full `dist/css/arco.css`. It lists only the stylesheets the imported components need, in full-bundle order (loading `css.js` per component would reorder the cascade and shift layouts). After importing a new Arco component, rerun the generator — `test/arco-styles.test.cjs` fails while it is stale.
- Preview watermark (employee no. + name + open time) is `WatermarkedPreview` in `src/components/FileTable.tsx`, styled by `.preview-watermark` in `src/index.css`.

## Testing philosophy in this repo

Tests here are deliberately end-to-end and isolation-heavy, not just unit tests:
- Every DB-touching test suite requires an explicit, separate test MySQL connection (`YF_TEST_DATABASE_URL`); nothing runs against the dev/business database. DB tests **skip** when it is unset, so a bare "exit code 0" does not prove a full pass — check failed/skipped/not-run counts are all zero.
- Browser tests build real artifacts (web `dist` + `TestHost`) and verify SHA-256 of what's actually under test, rejecting stale builds.
- `tests/Contracts/api-v1.json` is the authoritative frontend↔backend HTTP contract; update it alongside any route/method change.
- Typed contract: every JSON endpoint returns a named response record (`*Response`, `PageResponse<T>`, `EmptyResponse` — never `Task<object>` or anonymous objects), so `OpenApiContractTests` can generate `tests/Contracts/openapi-v1.json` (no DB; Swashbuckle lives only in the test project) and fail on any untyped endpoint. After changing a route, request or response type: `YF_UPDATE_OPENAPI=1 dotnet test .\tests\Yf.Api.Tests.csproj --filter OpenApiContractTests`, then in `web/` `npm run generate:api-types` (writes `src/api/generated/api-types.ts`; `test/api-types.test.cjs` fails while stale). Frontend calls are typed as `http.get<ApiResponses['GET /route/{id}']>(...)`; `src/api/types.ts` derives its types from the generated ones and only narrows enum-like string fields. Keep JSON shapes identical when converting: optional-when-null members need `[JsonIgnore(Condition = WhenWritingNull)]`.
