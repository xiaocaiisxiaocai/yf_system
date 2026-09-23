# Frontend legacy test migration coverage

This report maps each concrete legacy Node test registration to the Vitest test that replaces it. The source list expands dynamic registrations, so its total is the legacy runtime total rather than the number of static `test(...)` calls.

| Metric | Result |
|---|---:|
| Legacy cases | 299 |
| Mapped legacy cases | 299 |
| Unresolved legacy cases | 0 |
| Ambiguous legacy cases | 0 |
| Unique migrated targets | 299 |
| Mapped targets not passed in snapshot | 0 |

Vitest snapshot: `.artifacts/tests/frontend-migration/vitest-final.json` (314/314 passed).

## Resolution methods

- `auth-exact`: 33
- `auth-title-map`: 3
- `exact-title`: 225
- `files-map`: 38

## Unresolved cases

None.

## Ambiguous cases

None.

## Maintenance

Regenerate after a Vitest JSON run:

`node src/test/migration/build-migration-report.mjs --report .artifacts/tests/frontend-migration/vitest-final.json --require-passed`

Same-title cases resolve automatically. Authentication renames come from `migrated/auth/legacy-title-map.json`; file migrations use `migrated/files/migration-map.json`; cross-domain, split, or otherwise renamed cases belong in `manual-mappings.json`.
