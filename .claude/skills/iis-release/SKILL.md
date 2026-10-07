---
name: iis-release
description: Build, install, upgrade, back up or restore the IIS deployment package (publish-iis.ps1, install-iis.ps1, maintain-iis.ps1).
---

IIS packaging/maintenance: `scripts/publish-iis.ps1` builds into a unique folder under repo-root `deloy/` (ZIP only with `-CreateArchive`). Default private packages contain DB/JWT credentials from git-ignored `deploy/publish-defaults.local.json` and a fresh `App.BootstrapPassword` per package; bootstrap passwords are not reused from local defaults. Use `-ExternalConfigurationTemplate` for a package without DB/JWT/bootstrap secrets. HTTPS, non-root database access, secure cookies and a clean worktree are required by default; explicit private-development overrides are recorded in the manifest. `deploy/install-iis.ps1` installs a fresh site and `deploy/maintain-iis.ps1` handles Backup/Upgrade/Restore. See `deploy/README.md` for external `YF_CONFIG_PATH`, rollback boundaries and secret removal after initialization.
