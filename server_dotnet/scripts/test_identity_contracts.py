"""Identity/admin checks imported by test-isolated.py.

The caller owns the disposable database and API process. This module never discovers or
connects to another database and does not start services.
"""
import secrets


def _password():
    return "Identity-" + secrets.token_urlsafe(18)


def _refresh_cookie(client):
    return next(cookie.value for cookie in client.cookies if cookie.name == "refresh_token")


def run_identity_checks(client, Client, conn, check):
    # The observer route belongs to the isolated TestHost. The generated challenge still uses
    # the production renderer and guarantees at least one letter, so the former digit sampler
    # cannot reconstruct a valid answer.
    challenge = client.captcha()
    captcha_code = challenge["captchaCode"]
    check("identity captcha uses expanded randomized alphabet",
          len(captcha_code) == 6 and any(value.isalpha() for value in captcha_code)
          and all(value in "ACDEFHJKLMNPRTUVWXY347" for value in captcha_code))

    # Preserve the attempted employee number even when no users row can supply it.
    missing_employee = "missing_" + secrets.token_hex(5)
    failed_challenge = client.captcha()
    client.call("POST", "/api/v1/auth/login", {
        "employeeNo": missing_employee, "password": _password(),
        "captchaId": failed_challenge["captchaId"], "captchaCode": failed_challenge["captchaCode"]
    }, expected=401)
    with conn.cursor() as cursor:
        cursor.execute("SELECT employee_no FROM audit_logs WHERE action='LOGIN_FAILED' ORDER BY id DESC LIMIT 1")
        failed_employee_no = cursor.fetchone()[0]
    check("identity unknown employee login keeps attempted employee number in audit", failed_employee_no == missing_employee)

    # Ordinary account CRUD, including the singular role contract and a reset that revokes sessions.
    roles = client.call("GET", "/api/v1/admin/user-role-options")
    ordinary_role = next(role for role in roles if role["name"] not in ("系统管理员", "供应商人员"))
    crud_password = _password()
    employee = "crud_" + secrets.token_hex(5)
    account = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": employee, "password": crud_password, "realName": "契约临时用户",
        "email": employee + "@example.invalid", "departmentId": None, "roleId": ordinary_role["id"]
    })
    user_id = account["id"]
    updated = client.call("PUT", f"/api/v1/admin/users/{user_id}", {
        "realName": "契约更新用户", "email": employee + ".updated@example.invalid",
        "departmentId": None, "roleId": ordinary_role["id"]
    })
    check("identity internal account create and update", updated["realName"] == "契约更新用户" and updated["roleId"] == ordinary_role["id"])
    disabled = client.call("PUT", f"/api/v1/admin/users/{user_id}/status", {"status": "DISABLED"})
    client.call("PUT", f"/api/v1/admin/users/{user_id}/status", {"status": "ACTIVE"})
    client.call("PUT", f"/api/v1/admin/users/{user_id}/password", {"newPassword": _password()})
    client.call("PUT", f"/api/v1/admin/users/{user_id}/roles", {"roleIds": [ordinary_role["id"]]})
    client.call("DELETE", f"/api/v1/admin/users/{user_id}")
    check("identity account status reset role and delete", disabled["status"] == "DISABLED")

    # A delegated user may manage ordinary users but cannot read other admin domains or grant
    # the built-in administrator role above its authority ceiling.
    permissions = client.call("GET", "/api/v1/permissions")
    permission_ids = {item["code"]: item["id"] for item in permissions}
    unsafe_supplier_codes = ["user:manage", "role:manage", "supplier:manage", "config:manage", "log:view"]
    unsafe_supplier_ids = [permission_ids[code] for code in unsafe_supplier_codes]
    user_manage = permission_ids["user:manage"]
    role_page = client.call("GET", "/api/v1/admin/roles")
    supplier_role = next(role for role in role_page["list"] if role["name"] == "供应商人员")
    client.call("PUT", f"/api/v1/admin/roles/{supplier_role['id']}/permissions", {
        "permissionIds": supplier_role["permissionIds"] + unsafe_supplier_ids
    }, expected=400)
    check("identity supplier role rejects management permission assignment", True)
    delegated_role = client.call("POST", "/api/v1/admin/roles", {
        "name": "委派用户管理员-" + secrets.token_hex(4), "description": "isolated identity contract"
    })
    client.call("PUT", f"/api/v1/admin/roles/{delegated_role['id']}/permissions", {"permissionIds": [user_manage]})
    delegated_password = _password()
    delegated_employee = "delegate_" + secrets.token_hex(4)
    delegated = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": delegated_employee, "password": delegated_password, "realName": "委派管理员",
        "email": delegated_employee + "@example.invalid", "departmentId": None,
        "roleId": delegated_role["id"]
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (delegated["id"],))
    delegated_client = Client(client.base)
    delegated_client.login(delegated_employee, delegated_password)
    delegated_client.call("GET", "/api/v1/admin/users")
    delegated_client.call("GET", "/api/v1/admin/roles", expected=403)
    delegated_client.call("GET", "/api/v1/admin/suppliers", expected=403)
    admin_role = next(role for role in roles if role["name"] == "系统管理员")
    delegated_client.call("POST", "/api/v1/admin/users", {
        "employeeNo": "ceiling_" + secrets.token_hex(4), "password": _password(), "realName": "越权测试",
        "email": "ceiling." + secrets.token_hex(4) + "@example.invalid", "departmentId": None,
        "roleId": admin_role["id"]
    }, expected=403)
    check("identity delegated read boundaries and permission ceiling", True)

    # Rotate once, replay the old token from another client, then prove the whole family and the
    # access token minted by the successful rotation are revoked.
    refresh_password = _password()
    refresh_employee = "refresh_" + secrets.token_hex(4)
    refresh_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": refresh_employee, "password": refresh_password, "realName": "刷新重放用户",
        "email": refresh_employee + "@example.invalid", "departmentId": None, "roleId": ordinary_role["id"]
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (refresh_user["id"],))
    rotating = Client(client.base)
    rotating.login(refresh_employee, refresh_password)
    old_refresh = _refresh_cookie(rotating)
    rotated = rotating.call("POST", "/api/v1/auth/refresh")
    rotating.token = rotated["accessToken"]
    replay = Client(client.base)
    replay.call("POST", "/api/v1/auth/refresh", headers={"Cookie": "refresh_token=" + old_refresh}, expected=401)
    rotating.call("GET", "/api/v1/auth/profile", expected=401)
    rotating.call("POST", "/api/v1/auth/refresh", expected=401)
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id=%s AND revoked=0", (refresh_user["id"],))
        active_family_tokens = cursor.fetchone()[0]
    check("identity refresh rotation replay revokes session family", active_family_tokens == 0)

    # Disabling a supplier blocks an already-issued supplier access token and future refresh.
    supplier = client.call("POST", "/api/v1/admin/suppliers", {
        "name": "会话禁用供应商-" + secrets.token_hex(4), "remark": "isolated identity contract"
    })
    supplier_password = _password()
    supplier_employee = "supplier_" + secrets.token_hex(4)
    supplier_user = client.call("POST", f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
        "employeeNo": supplier_employee, "password": supplier_password, "realName": "供应商会话用户",
        "email": supplier_employee + "@example.invalid"
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (supplier_user["id"],))
    supplier_client = Client(client.base)
    supplier_client.login(supplier_employee, supplier_password)
    with conn.cursor() as cursor:
        cursor.execute("SELECT permission_id FROM role_permissions WHERE role_id=%s", (supplier_role["id"],))
        existing_unsafe_ids = {row[0] for row in cursor.fetchall()}
        inserted_unsafe_ids = [permission_id for permission_id in unsafe_supplier_ids
                               if permission_id not in existing_unsafe_ids]
        cursor.executemany("INSERT INTO role_permissions(role_id,permission_id) VALUES(%s,%s)",
                           [(supplier_role["id"], permission_id) for permission_id in inserted_unsafe_ids])
    try:
        supplier_client.call("GET", "/api/v1/admin/users", expected=403)
        supplier_client.call("GET", "/api/v1/admin/roles", expected=403)
        supplier_client.call("GET", "/api/v1/admin/suppliers", expected=403)
        supplier_client.call("GET", "/api/v1/admin/system/configs", expected=403)
        supplier_client.call("GET", "/api/v1/admin/audit-logs", expected=403)
    finally:
        with conn.cursor() as cursor:
            if inserted_unsafe_ids:
                cursor.executemany("DELETE FROM role_permissions WHERE role_id=%s AND permission_id=%s",
                                   [(supplier_role["id"], permission_id) for permission_id in inserted_unsafe_ids])
    check("identity supplier cannot cross five internal admin domains after unsafe grants", True)
    client.call("PUT", f"/api/v1/admin/suppliers/{supplier['id']}/status", {"status": "DISABLED"})
    supplier_client.call("GET", "/api/v1/auth/profile", expected=401)
    supplier_client.call("POST", "/api/v1/auth/refresh", expected=401)
    client.call("PUT", f"/api/v1/admin/suppliers/{supplier['id']}/status", {"status": "ACTIVE"})
    check("identity disabled supplier invalidates account session", True)
