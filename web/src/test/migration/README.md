# Legacy test migration map

`previous-cases.json` is the immutable inventory of the 299 concrete legacy Node test registrations, including the 18 registrations produced by top-level loops.

`build-migration-report.mjs` reconciles that inventory against a Vitest JSON report. It resolves unchanged titles directly, reads authentication title changes from `../migrated/auth/legacy-title-map.json`, reads explicit file-domain mappings from `../migrated/files/migration-map.json`, and applies exceptional or one-to-many replacements from `manual-mappings.json`.

Run it from `web` after producing the Vitest JSON report:

```powershell
node src/test/migration/build-migration-report.mjs --report .artifacts/tests/frontend-migration/vitest-final.json --require-passed
```

The command regenerates:

- `legacy-case-map.json`: all 299 old cases with their concrete new test file, title, full name, and snapshot status.
- `migration-report.json`: machine-readable counts, unresolved/ambiguous entries, mapping integrity, and failed mapped targets.
- `MIGRATION_REPORT.md`: compact reviewer-facing report.

The execution report is authoritative for both registration and pass/fail status. Do not substitute `vitest list`: static listing does not expand the repository's loop-generated tests. The command fails when the source inventory is not exactly 299, a case is missing or ambiguous, an override points to a nonexistent old case, or `--require-passed` receives any unsuccessful Vitest run or mapped target. Extra new tests are allowed and reported through the Vitest snapshot totals.

For a renamed or split case, add an entry to `manual-mappings.json`:

```json
[
  {
    "oldFile": "web/test/example.test.cjs",
    "oldLine": 10,
    "oldTitle": "legacy behavior",
    "newCases": [
      {
        "newFile": "web/src/test/migrated/example/new.test.tsx",
        "newTitle": "replacement behavior"
      }
    ],
    "note": "Why the contract was renamed or split."
  }
]
```
