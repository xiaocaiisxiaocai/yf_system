"""Install legacy built-in roles only in disposable ``yf_test_`` databases.

Production initialization intentionally creates only the administrator role.
The broader regression suites still exercise historical role-specific behavior,
so they opt in to the other three baseline roles after verifying the fresh
initialization result.
"""

import json
from pathlib import Path


BASELINE = Path(__file__).resolve().parents[1] / "Yf.Api/Infrastructure/schema-baseline.json"
ADMIN_ROLE_NAME = "系统管理员"
LEGACY_TEST_ROLE_NAMES = ("项目管理员", "内部成员", "供应商人员")
TEST_ROLE_DENIED_GRANTS = {"供应商人员": {"project:confirm"}}


def _require_disposable_database(connection):
    with connection.cursor() as cursor:
        cursor.execute("SELECT DATABASE()")
        row = cursor.fetchone()
    database = row[0] if row else None
    if not isinstance(database, str) or not database.startswith("yf_test_"):
        raise RuntimeError(
            "Legacy role fixtures may only modify a selected yf_test_ database."
        )
    return database


def assert_admin_only_initialization(connection):
    """Prove a fresh initialization contains one admin user and one admin role."""
    _require_disposable_database(connection)
    with connection.cursor() as cursor:
        cursor.execute("SELECT employee_no FROM users ORDER BY id")
        users = tuple(row[0] for row in cursor.fetchall())
        cursor.execute("SELECT name FROM roles ORDER BY id")
        roles = tuple(row[0] for row in cursor.fetchall())
        cursor.execute(
            """
            SELECT u.employee_no, r.name
            FROM user_roles ur
            JOIN users u ON u.id=ur.user_id
            JOIN roles r ON r.id=ur.role_id
            ORDER BY u.id, r.id
            """
        )
        assignments = tuple(cursor.fetchall())
    if users != ("admin",):
        raise AssertionError(f"fresh initialization users were {users!r}, expected only admin")
    if roles != (ADMIN_ROLE_NAME,):
        raise AssertionError(
            f"fresh initialization roles were {roles!r}, expected only {ADMIN_ROLE_NAME}"
        )
    if assignments != (("admin", ADMIN_ROLE_NAME),):
        raise AssertionError(
            "fresh initialization must assign only the system administrator role to admin"
        )


def install_legacy_test_roles(connection):
    """Add the three historical built-in roles and their baseline grants for tests."""
    _require_disposable_database(connection)
    baseline = json.loads(BASELINE.read_text(encoding="utf-8-sig"))
    seeds = baseline["seeds"]
    baseline_roles = {row["name"]: row for row in seeds["roles"]}
    if set(baseline_roles) != {ADMIN_ROLE_NAME, *LEGACY_TEST_ROLE_NAMES}:
        raise RuntimeError("Unexpected role set in schema baseline; update the test fixture explicitly.")

    permission_codes = {row["id"]: row["code"] for row in seeds["permissions"]}
    grants_by_role = {name: [] for name in LEGACY_TEST_ROLE_NAMES}
    baseline_role_names_by_id = {row["id"]: row["name"] for row in seeds["roles"]}
    for grant in seeds["role_permissions"]:
        role_name = baseline_role_names_by_id.get(grant["role_id"])
        if role_name in grants_by_role:
            grants_by_role[role_name].append(permission_codes[grant["permission_id"]])

    with connection.cursor() as cursor:
        cursor.execute(
            "SELECT id,name,description,is_built_in,status,created_at,updated_at "
            "FROM roles WHERE name=%s",
            (ADMIN_ROLE_NAME,),
        )
        admin_before = cursor.fetchone()
        if admin_before is None:
            raise AssertionError("system administrator role is missing before test fixture setup")
        cursor.execute(
            """
            SELECT p.code
            FROM role_permissions rp
            JOIN permissions p ON p.id=rp.permission_id
            WHERE rp.role_id=%s
            ORDER BY p.code
            """,
            (admin_before[0],),
        )
        admin_grants_before = tuple(row[0] for row in cursor.fetchall())

    try:
        connection.begin()
        with connection.cursor() as cursor:
            for name in LEGACY_TEST_ROLE_NAMES:
                row = baseline_roles[name]
                cursor.execute("SELECT id,is_built_in,status FROM roles WHERE name=%s", (name,))
                existing = cursor.fetchone()
                if existing is None:
                    cursor.execute(
                        """
                        INSERT INTO roles
                            (id,name,description,is_built_in,status,created_at,updated_at)
                        VALUES (%s,%s,%s,%s,%s,%s,%s)
                        """,
                        (
                            row["id"],
                            row["name"],
                            row["description"],
                            row["is_built_in"],
                            row["status"],
                            row["created_at"],
                            row["updated_at"],
                        ),
                    )
                    role_id = row["id"]
                else:
                    role_id, is_built_in, status = existing
                    if not is_built_in or status != "ACTIVE":
                        raise AssertionError(
                            f"existing test role {name} does not match the built-in active baseline"
                        )

                codes = [
                    code for code in grants_by_role[name]
                    if code not in TEST_ROLE_DENIED_GRANTS.get(name, set())
                ]
                if not codes:
                    raise RuntimeError(f"schema baseline has no permissions for test role {name}")
                placeholders = ",".join(["%s"] * len(codes))
                cursor.execute(
                    f"SELECT id,code FROM permissions WHERE code IN ({placeholders})",
                    tuple(codes),
                )
                actual_permissions = {code: permission_id for permission_id, code in cursor.fetchall()}
                missing = sorted(set(codes) - set(actual_permissions))
                if missing:
                    raise AssertionError(
                        f"test database is missing baseline permissions for {name}: {missing}"
                    )
                cursor.executemany(
                    "INSERT IGNORE INTO role_permissions(role_id,permission_id) VALUES(%s,%s)",
                    [(role_id, actual_permissions[code]) for code in codes],
                )
        connection.commit()
    except Exception:
        connection.rollback()
        raise

    with connection.cursor() as cursor:
        cursor.execute(
            "SELECT id,name,description,is_built_in,status,created_at,updated_at "
            "FROM roles WHERE name=%s",
            (ADMIN_ROLE_NAME,),
        )
        admin_after = cursor.fetchone()
        cursor.execute(
            """
            SELECT p.code
            FROM role_permissions rp
            JOIN permissions p ON p.id=rp.permission_id
            WHERE rp.role_id=%s
            ORDER BY p.code
            """,
            (admin_before[0],),
        )
        admin_grants_after = tuple(row[0] for row in cursor.fetchall())
        cursor.execute(
            "SELECT name FROM roles WHERE name IN (%s,%s,%s) ORDER BY id",
            LEGACY_TEST_ROLE_NAMES,
        )
        installed = {row[0] for row in cursor.fetchall()}
        installed_grants = {}
        for name in LEGACY_TEST_ROLE_NAMES:
            cursor.execute(
                """
                SELECT p.code
                FROM role_permissions rp
                JOIN roles r ON r.id=rp.role_id
                JOIN permissions p ON p.id=rp.permission_id
                WHERE r.name=%s
                ORDER BY p.code
                """,
                (name,),
            )
            installed_grants[name] = tuple(row[0] for row in cursor.fetchall())
    if admin_after != admin_before or admin_grants_after != admin_grants_before:
        raise AssertionError("legacy test role setup modified the system administrator role")
    if installed != set(LEGACY_TEST_ROLE_NAMES):
        raise AssertionError("legacy test role setup did not install all three expected roles")
    for name, expected in grants_by_role.items():
        expected = [
            code for code in expected
            if code not in TEST_ROLE_DENIED_GRANTS.get(name, set())
        ]
        if installed_grants[name] != tuple(sorted(expected)):
            raise AssertionError(f"legacy test role {name} does not have its exact baseline grants")
