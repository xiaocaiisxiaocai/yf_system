"""Identity/admin checks imported by test-isolated.py.

The caller owns the disposable database and API process. This module never discovers or
connects to another database and does not start services.
"""
import json
import secrets
import urllib.parse


def _password():
    return "Yf9!" + secrets.token_urlsafe(9)


def _refresh_cookie(client):
    return next(cookie.value for cookie in client.cookies if cookie.name == "refresh_token")


def _login_attempt(Client, base, employee_no, password, expected=200):
    actor = Client(base)
    result = actor.call("POST", "/api/v1/auth/login", {
        "employeeNo": employee_no,
        "password": password,
    }, expected=expected)
    if expected == 200:
        actor.token = result["accessToken"]
    return actor, result


def run_identity_checks(client, Client, conn, check):
    # Preserve the attempted employee number even when no users row can supply it.
    missing_employee = "missing_" + secrets.token_hex(5)
    missing_result = client.call("POST", "/api/v1/auth/login", {
        "employeeNo": missing_employee, "password": _password(),
    }, expected=401)
    with conn.cursor() as cursor:
        cursor.execute("SELECT employee_no FROM audit_logs WHERE action='LOGIN_FAILED' ORDER BY id DESC LIMIT 1")
        failed_employee_no = cursor.fetchone()[0]
    check("identity direct login failure is unauthorized without CAPTCHA",
          missing_result["code"] == 40101 and failed_employee_no == missing_employee)

    # Ordinary account CRUD, including omitted-versus-null department updates, the singular
    # role contract, and a reset that revokes sessions.
    roles = client.call("GET", "/api/v1/admin/user-role-options")
    ordinary_role = next(role for role in roles if role["name"] not in ("系统管理员", "供应商人员"))
    department = client.call("POST", "/api/v1/admin/departments", {
        "name": "契约测试组织-" + secrets.token_hex(4), "parentId": None, "sortNo": 0
    })
    boundary_suffix = secrets.token_hex(4)
    boundary_payload = {
        "realName": "密码边界用户", "email": f"password-boundary-{boundary_suffix}@example.invalid",
        "departmentId": department["id"], "roleId": ordinary_role["id"],
    }
    too_short = client.call("POST", "/api/v1/admin/users", {
        **boundary_payload, "employeeNo": "pw5_" + boundary_suffix, "password": "A1!bc",
    }, expected=400)
    minimum = client.call("POST", "/api/v1/admin/users", {
        **boundary_payload, "employeeNo": "pw6_" + boundary_suffix, "password": "G7!mQx",
    })
    maximum = client.call("POST", "/api/v1/admin/users", {
        **boundary_payload, "employeeNo": "pw20_" + boundary_suffix,
        "password": "A1!bcDefGhijkLmNopQr",
    })
    too_long = client.call("POST", "/api/v1/admin/users", {
        **boundary_payload, "employeeNo": "pw21_" + boundary_suffix,
        "password": "A1!bcDefGhijkLmNopQrs",
    }, expected=400)
    weak = client.call("POST", "/api/v1/admin/users", {
        **boundary_payload, "employeeNo": "pwweak_" + boundary_suffix, "password": "admin!",
    }, expected=400)
    client.call("DELETE", f"/api/v1/admin/users/{minimum['id']}")
    client.call("DELETE", f"/api/v1/admin/users/{maximum['id']}")
    check("identity password policy accepts 6 and 20 characters and rejects 5, 21, and weak passwords",
          too_short["code"] == 40001 and too_long["code"] == 40001
          and weak["code"] == 40001 and minimum["id"] != maximum["id"])

    admin_employee = client.call("GET", "/api/v1/auth/profile")["user"]["employeeNo"]
    _, wrong_password = _login_attempt(Client, client.base, admin_employee, _password(), expected=401)
    check("identity wrong password is unauthorized rather than a CAPTCHA challenge",
          wrong_password["code"] == 40101)

    crud_password = _password()
    employee = "crud_" + secrets.token_hex(5)
    account = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": employee, "password": crud_password, "realName": "契约临时用户",
        "email": employee + "@example.invalid", "departmentId": department["id"],
        "roleId": ordinary_role["id"]
    })
    user_id = account["id"]
    retained = client.call("PUT", f"/api/v1/admin/users/{user_id}", {
        "realName": "契约保留组织用户", "roleId": ordinary_role["id"]
    })
    retained_page = client.call("GET", "/api/v1/admin/users?" + urllib.parse.urlencode({
        "keyword": employee,
    }))
    with conn.cursor() as cursor:
        cursor.execute("SELECT department_id FROM users WHERE id=%s", (user_id,))
        retained_department_id = cursor.fetchone()[0]
    invalid_zero_role = client.call("PUT", f"/api/v1/admin/users/{user_id}", {
        "roleId": 0,
    }, expected=400)
    clearing = client.call("PUT", f"/api/v1/admin/users/{user_id}", {
        "departmentId": None, "roleId": ordinary_role["id"]
    }, expected=400)
    updated = client.call("PUT", f"/api/v1/admin/users/{user_id}", {
        "realName": "契约更新用户", "email": employee + ".updated@example.invalid",
        "roleId": ordinary_role["id"]
    })
    updated_page = client.call("GET", "/api/v1/admin/users?" + urllib.parse.urlencode({
        "keyword": employee,
    }))
    with conn.cursor() as cursor:
        cursor.execute("SELECT department_id FROM users WHERE id=%s", (user_id,))
        persisted_department_id = cursor.fetchone()[0]
        cursor.execute("SELECT detail FROM audit_logs WHERE action='USER_UPDATE' AND target_type='user' AND target_id=%s ORDER BY id DESC LIMIT 1", (str(user_id),))
        update_detail = json.loads(cursor.fetchone()[0])
    check("identity omitted department update preserves the assignment",
          retained["departmentId"] == department["id"]
          and retained_page["total"] == 1
          and retained_page["list"][0]["departmentId"] == department["id"]
          and retained_department_id == department["id"])
    check("identity explicit null department update is rejected and the required assignment is preserved",
          clearing["code"] == 40001
          and updated["realName"] == "契约更新用户" and updated["roleId"] == ordinary_role["id"]
          and updated["departmentId"] == department["id"]
          and updated_page["total"] == 1 and updated_page["list"][0]["departmentId"] == department["id"]
          and persisted_department_id == department["id"]
          and update_detail["changedFields"] == ["realName", "email"]
          and update_detail["oldDepartmentId"] == department["id"]
          and update_detail["newDepartmentId"] == department["id"])
    check("identity explicit zero role is rejected", invalid_zero_role["code"] == 40001)
    email_keyword = urllib.parse.quote(updated["email"])
    email_results = client.call("GET", f"/api/v1/admin/users?keyword={email_keyword}")
    check("identity internal account search includes email", email_results["total"] == 1 and email_results["list"][0]["id"] == user_id)
    disabled = client.call("PUT", f"/api/v1/admin/users/{user_id}/status", {"status": "DISABLED"})
    client.call("PUT", f"/api/v1/admin/users/{user_id}/status", {"status": "ACTIVE"})
    client.call("PUT", f"/api/v1/admin/users/{user_id}/password", {"newPassword": _password()})
    client.call("PUT", f"/api/v1/admin/users/{user_id}/roles", {"roleIds": [ordinary_role["id"]]})
    client.call("DELETE", f"/api/v1/admin/users/{user_id}")
    check("identity account status reset role and delete", disabled["status"] == "DISABLED")

    # Self-service profile updates are part of the authentication audit category and retain
    # structured field details for the frontend summary.
    profile = client.call("GET", "/api/v1/auth/profile")
    profile_email = "profile." + secrets.token_hex(5) + "@example.invalid"
    updated_profile = client.call("PUT", "/api/v1/auth/profile", {"email": profile_email})
    profile_logs = client.call("GET", "/api/v1/admin/audit-logs?category=AUTH&action=PROFILE_UPDATE")
    profile_rows = [row for row in profile_logs["list"] if row["userId"] == profile["user"]["id"]]
    check("identity profile update is visible in auth audit category",
          updated_profile["user"]["email"] == profile_email and len(profile_rows) == 1
          and profile_rows[0]["detail"]["changedFields"] == ["email"])

    # A delegated user may manage ordinary users but cannot read other admin domains or grant
    # the built-in administrator role above its authority ceiling.
    permissions = client.call("GET", "/api/v1/permissions")
    permission_ids = {item["code"]: item["id"] for item in permissions}
    unsafe_supplier_codes = ["user:manage", "role:manage", "supplier:manage", "config:manage", "log:view"]
    unsafe_supplier_ids = [permission_ids[code] for code in unsafe_supplier_codes]
    user_manage = permission_ids["user:manage"]
    role_page = client.call("GET", "/api/v1/admin/roles")
    supplier_role = next(role for role in role_page["list"] if role["name"] == "供应商人员")
    admin_role = next(role for role in role_page["list"] if role["name"] == "系统管理员")
    check("identity supplier role starts unassigned for built-in deletion coverage",
          supplier_role["assignedUserCount"] == 0)
    delete_built_in = client.call(
        "DELETE", f"/api/v1/admin/roles/{supplier_role['id']}", expected=400)
    supplier_role_after_delete_rejection = next(
        role for role in client.call("GET", "/api/v1/admin/roles")["list"]
        if role["id"] == supplier_role["id"])
    check("identity unassigned built-in supplier role cannot be deleted",
          delete_built_in["code"] == 40001
          and supplier_role_after_delete_rejection["isBuiltIn"] is True
          and supplier_role_after_delete_rejection["permissionIds"] == supplier_role["permissionIds"])
    client.call("PUT", f"/api/v1/admin/roles/{supplier_role['id']}/permissions", {
        "permissionIds": supplier_role["permissionIds"] + unsafe_supplier_ids
    }, expected=400)
    check("identity supplier role rejects management permission assignment", True)
    limited_role_manager = client.call("POST", "/api/v1/admin/roles", {
        "name": "受限角色管理员-" + secrets.token_hex(4), "description": "role capability projection"
    })
    client.call("PUT", f"/api/v1/admin/roles/{limited_role_manager['id']}/permissions", {
        "permissionIds": [permission_ids["rbac:role"], permission_ids["role:manage"]]
    })
    limited_role_manager_password = _password()
    limited_role_manager_employee = "role_delegate_" + secrets.token_hex(4)
    limited_role_manager_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": limited_role_manager_employee, "password": limited_role_manager_password,
        "realName": "受限角色管理员", "email": limited_role_manager_employee + "@example.invalid",
        "departmentId": department["id"], "roleId": limited_role_manager["id"]
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (limited_role_manager_user["id"],))
    limited_role_client = Client(client.base)
    limited_role_client.login(limited_role_manager_employee, limited_role_manager_password)
    limited_role_page = limited_role_client.call("GET", "/api/v1/admin/roles?pageSize=100")
    limited_roles = {role["id"]: role for role in limited_role_page["list"]}
    limited_permissions = {item["code"]: item for item in limited_role_client.call("GET", "/api/v1/permissions")}
    check("identity role capability projection marks protected roles read-only and only owned permissions grantable",
          limited_roles[limited_role_manager["id"]]["canManage"] is True
          and limited_roles[admin_role["id"]]["canManage"] is False
          and limited_permissions["role:manage"]["grantable"] is True
          and limited_permissions["project:confirm"]["grantable"] is False
          and limited_permissions["project:confirm"]["supplierAssignable"] is False)
    delegated_role = client.call("POST", "/api/v1/admin/roles", {
        "name": "委派用户管理员-" + secrets.token_hex(4), "description": "isolated identity contract"
    })
    client.call("PUT", f"/api/v1/admin/roles/{delegated_role['id']}/permissions", {"permissionIds": [user_manage]})
    delegated_password = _password()
    delegated_employee = "delegate_" + secrets.token_hex(4)
    delegated = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": delegated_employee, "password": delegated_password, "realName": "委派管理员",
        "email": delegated_employee + "@example.invalid", "departmentId": department["id"],
        "roleId": delegated_role["id"]
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (delegated["id"],))
    delegated_client = Client(client.base)
    delegated_client.login(delegated_employee, delegated_password)
    delegated_client.call("GET", "/api/v1/admin/users")
    delegated_role_options = delegated_client.call("GET", "/api/v1/admin/user-role-options")
    delegated_client.call("GET", "/api/v1/admin/roles", expected=403)
    delegated_client.call("GET", "/api/v1/admin/suppliers", expected=403)
    delegated_client.call("POST", "/api/v1/admin/users", {
        "employeeNo": "ceiling_" + secrets.token_hex(4), "password": _password(), "realName": "越权测试",
        "email": "ceiling." + secrets.token_hex(4) + "@example.invalid", "departmentId": department["id"],
        "roleId": admin_role["id"]
    }, expected=403)
    check("identity delegated read boundaries and permission ceiling",
          delegated_role["id"] in {role["id"] for role in delegated_role_options}
          and admin_role["id"] not in {role["id"] for role in delegated_role_options})

    secondary_admin = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": "secondary_admin_" + secrets.token_hex(3), "password": _password(),
        "realName": "停用管理员", "email": "secondary.admin." + secrets.token_hex(4) + "@example.invalid",
        "departmentId": department["id"], "roleId": admin_role["id"]
    })
    client.call("PUT", f"/api/v1/admin/users/{secondary_admin['id']}/status", {"status": "DISABLED"})
    reassigned_secondary = client.call("PUT", f"/api/v1/admin/users/{secondary_admin['id']}/roles", {
        "roleIds": [ordinary_role["id"]]
    })
    client.call("DELETE", f"/api/v1/admin/users/{secondary_admin['id']}")
    check("identity disabled secondary administrator can be reassigned without weakening the last active administrator",
          reassigned_secondary is not None)

    # Rotate once, replay the old token from another client, then prove the whole family and the
    # access token minted by the successful rotation are revoked.
    refresh_password = _password()
    refresh_employee = "refresh_" + secrets.token_hex(4)
    refresh_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": refresh_employee, "password": refresh_password, "realName": "刷新重放用户",
        "email": refresh_employee + "@example.invalid", "departmentId": department["id"], "roleId": ordinary_role["id"]
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

    # Disabling one supplier revokes every account/session while another supplier keeps working.
    supplier = client.call("POST", "/api/v1/admin/suppliers", {
        "name": "会话禁用供应商-" + secrets.token_hex(4), "remark": "isolated identity contract"
    })
    supplier_password = _password()
    supplier_employee = "supplier_" + secrets.token_hex(4)
    supplier_user = client.call("POST", f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
        "employeeNo": supplier_employee, "password": supplier_password, "realName": "供应商会话用户",
        "email": supplier_employee + "@example.invalid"
    })
    check("identity supplier account creation still works after built-in deletion rejection",
          supplier_user["supplierId"] == supplier["id"])
    client.call("PUT", f"/api/v1/admin/roles/{supplier_role['id']}/status", {"status": "DISABLED"}, expected=400)
    client.call("PUT", f"/api/v1/admin/supplier-accounts/{supplier_user['id']}/status", {"status": "DISABLED"})
    client.call("PUT", f"/api/v1/admin/roles/{supplier_role['id']}/status", {"status": "DISABLED"})
    blocked_employee = "blocked_supplier_" + secrets.token_hex(3)
    client.call("POST", f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
        "employeeNo": blocked_employee, "password": _password(), "realName": "禁用角色账号",
        "email": blocked_employee + "@example.invalid"
    }, expected=400)
    client.call("PUT", f"/api/v1/admin/supplier-accounts/{supplier_user['id']}/status", {"status": "ACTIVE"}, expected=400)
    client.call("PUT", f"/api/v1/admin/roles/{supplier_role['id']}/status", {"status": "ACTIVE"})
    client.call("PUT", f"/api/v1/admin/supplier-accounts/{supplier_user['id']}/status", {"status": "ACTIVE"})
    check("identity supplier role protects active accounts and gates account creation or enable", True)
    supplier_peer_password = _password()
    supplier_peer_employee = "supplier_peer_" + secrets.token_hex(4)
    supplier_peer_user = client.call("POST", f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
        "employeeNo": supplier_peer_employee, "password": supplier_peer_password,
        "realName": "供应商第二会话用户", "email": supplier_peer_employee + "@example.invalid"
    })
    control_supplier = client.call("POST", "/api/v1/admin/suppliers", {
        "name": "会话控制供应商-" + secrets.token_hex(4), "remark": "isolated identity control"
    })
    control_supplier_password = _password()
    control_supplier_employee = "supplier_control_" + secrets.token_hex(4)
    control_supplier_user = client.call(
        "POST", f"/api/v1/admin/suppliers/{control_supplier['id']}/accounts", {
            "employeeNo": control_supplier_employee, "password": control_supplier_password,
            "realName": "其他供应商控制用户", "email": control_supplier_employee + "@example.invalid"
        })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id IN (%s,%s,%s)",
                       (supplier_user["id"], supplier_peer_user["id"], control_supplier_user["id"]))
    supplier_client = Client(client.base)
    supplier_client.login(supplier_employee, supplier_password)
    supplier_second_session = Client(client.base)
    supplier_second_session.login(supplier_employee, supplier_password)
    supplier_peer_client = Client(client.base)
    supplier_peer_client.login(supplier_peer_employee, supplier_peer_password)
    control_supplier_client = Client(client.base)
    control_supplier_client.login(control_supplier_employee, control_supplier_password)
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
    target_supplier_sessions = [
        (supplier_client, supplier_client.token, _refresh_cookie(supplier_client)),
        (supplier_second_session, supplier_second_session.token, _refresh_cookie(supplier_second_session)),
        (supplier_peer_client, supplier_peer_client.token, _refresh_cookie(supplier_peer_client)),
    ]
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id IN (%s,%s) AND revoked=0",
                       (supplier_user["id"], supplier_peer_user["id"]))
        active_target_sessions_before_disable = cursor.fetchone()[0]
    client.call("PUT", f"/api/v1/admin/suppliers/{supplier['id']}/status", {"status": "DISABLED"})
    for old_client, _, old_refresh in target_supplier_sessions:
        old_client.call("GET", "/api/v1/auth/profile", expected=401)
        Client(client.base).call("POST", "/api/v1/auth/refresh",
                                 headers={"Cookie": "refresh_token=" + old_refresh}, expected=401)
    unaffected_profile = control_supplier_client.call("GET", "/api/v1/auth/profile")
    unaffected_refresh = control_supplier_client.call("POST", "/api/v1/auth/refresh")
    control_supplier_client.token = unaffected_refresh["accessToken"]
    unaffected_profile_after_refresh = control_supplier_client.call("GET", "/api/v1/auth/profile")
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id IN (%s,%s) AND revoked=0",
                       (supplier_user["id"], supplier_peer_user["id"]))
        active_supplier_sessions_while_disabled = cursor.fetchone()[0]
        cursor.execute("SELECT detail FROM audit_logs WHERE action='SUPPLIER_STATUS' AND target_id=%s ORDER BY id DESC LIMIT 1",
                       (str(supplier["id"]),))
        disabled_supplier_audit = cursor.fetchone()[0]
    client.call("PUT", f"/api/v1/admin/suppliers/{supplier['id']}/status", {"status": "ACTIVE"})
    for old_client, _, old_refresh in target_supplier_sessions:
        old_client.call("GET", "/api/v1/auth/profile", expected=401)
        Client(client.base).call("POST", "/api/v1/auth/refresh",
                                 headers={"Cookie": "refresh_token=" + old_refresh}, expected=401)
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id IN (%s,%s) AND revoked=0",
                       (supplier_user["id"], supplier_peer_user["id"]))
        active_supplier_sessions_before_relogin = cursor.fetchone()[0]
    resumed_supplier = Client(client.base)
    resumed_supplier.login(supplier_employee, supplier_password)
    resumed_profile = resumed_supplier.call("GET", "/api/v1/auth/profile")
    resumed_supplier_peer = Client(client.base)
    resumed_supplier_peer.login(supplier_peer_employee, supplier_peer_password)
    resumed_peer_profile = resumed_supplier_peer.call("GET", "/api/v1/auth/profile")
    unaffected_profile_after_reenable = control_supplier_client.call("GET", "/api/v1/auth/profile")
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id IN (%s,%s) AND revoked=0",
                       (supplier_user["id"], supplier_peer_user["id"]))
        active_supplier_sessions_after_login = cursor.fetchone()[0]
    disabled_supplier_detail = json.loads(disabled_supplier_audit)
    check("identity disabled supplier revokes every account session without affecting another supplier",
          active_target_sessions_before_disable == 3
          and active_supplier_sessions_while_disabled == 0
          and active_supplier_sessions_before_relogin == 0
          and active_supplier_sessions_after_login == 2
          and resumed_profile["user"]["id"] == supplier_user["id"]
          and resumed_peer_profile["user"]["id"] == supplier_peer_user["id"]
          and unaffected_profile["user"]["id"] == control_supplier_user["id"]
          and unaffected_profile_after_refresh["user"]["id"] == control_supplier_user["id"]
          and unaffected_profile_after_reenable["user"]["id"] == control_supplier_user["id"]
          and disabled_supplier_detail["sessionsRevoked"] is True
          and disabled_supplier_detail["revokedSessionCount"] >= 3
          and all(secret not in disabled_supplier_audit
                  for secret in ([supplier_password, supplier_peer_password, control_supplier_password]
                                 + [secret for _, access, refresh in target_supplier_sessions
                                    for secret in (access, refresh)])))

    # Logout records only a real session revocation. A repeated request with the same, now-revoked
    # access token must not duplicate the audit row or disclose either token/session identifier.
    logout_password = _password()
    logout_employee = "logout_" + secrets.token_hex(4)
    logout_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": logout_employee, "password": logout_password, "realName": "注销审计用户",
        "email": logout_employee + "@example.invalid", "departmentId": department["id"], "roleId": ordinary_role["id"]
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (logout_user["id"],))
    logout_client = Client(client.base)
    logout_client.login(logout_employee, logout_password)
    logout_access = logout_client.token
    logout_refresh = _refresh_cookie(logout_client)
    logout_client.call("POST", "/api/v1/auth/logout")
    logout_client.call("POST", "/api/v1/auth/logout")
    with conn.cursor() as cursor:
        cursor.execute("SELECT target_type,target_id,detail,ip FROM audit_logs WHERE user_id=%s AND action='LOGOUT' ORDER BY id", (logout_user["id"],))
        logout_rows = cursor.fetchall()
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id=%s AND revoked=0", (logout_user["id"],))
        active_logout_tokens = cursor.fetchone()[0]
    logout_detail = json.loads(logout_rows[0][2]) if len(logout_rows) == 1 else {}
    check("identity logout audits one valid revocation without credential disclosure",
          active_logout_tokens == 0 and len(logout_rows) == 1
          and logout_rows[0][0] is None and logout_rows[0][1] is None
          and logout_detail.get("auditContext", {}).get("source") == "HTTP"
          and bool(logout_rows[0][3])
          and all(secret not in logout_rows[0][2]
                  for secret in (logout_password, logout_access, logout_refresh)))

    run_identity_lifecycle_checks(client, Client, conn, check)


def run_identity_lifecycle_checks(client, Client, conn, check):
    """Exercise password, session, RBAC, and audit postconditions through public APIs."""
    suffix = secrets.token_hex(5)
    admin_profile = client.call("GET", "/api/v1/auth/profile")["user"]
    permissions = client.call("GET", "/api/v1/permissions")
    user_manage = next(item["id"] for item in permissions if item["code"] == "user:manage")
    ordinary_role = next(role for role in client.call("GET", "/api/v1/admin/user-role-options")
                         if role["name"] == "内部成员")
    lifecycle_department = client.call("POST", "/api/v1/admin/departments", {
        "name": "身份生命周期组织-" + suffix,
        "parentId": None,
        "sortNo": 0,
    })

    lifecycle_role = client.call("POST", "/api/v1/admin/roles", {
        "name": "身份生命周期角色-" + suffix,
        "description": "password session and live RBAC acceptance",
    })
    role_id = lifecycle_role["id"]
    client.call("PUT", f"/api/v1/admin/roles/{role_id}/permissions", {
        "permissionIds": [user_manage],
    })
    employee_no = "lifecycle_" + suffix
    password0 = _password()
    lifecycle_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": employee_no,
        "password": password0,
        "realName": "身份生命周期用户",
        "email": employee_no + "@example.invalid",
        "departmentId": lifecycle_department["id"],
        "roleId": role_id,
    })
    user_id = lifecycle_user["id"]
    with conn.cursor() as cursor:
        cursor.execute("SELECT password_hash FROM users WHERE id=%s", (user_id,))
        initial_hash = cursor.fetchone()[0]

    # First-password flow: only profile/password/logout are available, and changing the
    # password revokes both halves of the session before the new password can be used.
    actor, first_login = _login_attempt(Client, client.base, employee_no, password0)
    forced = actor.call("GET", "/api/v1/project-groups", expected=403)
    refresh0 = _refresh_cookie(actor)
    password1 = _password()
    actor.call("PUT", "/api/v1/auth/password", {
        "oldPassword": password0,
        "newPassword": password1,
    })
    actor.call("GET", "/api/v1/auth/profile", expected=401)
    Client(client.base).call("POST", "/api/v1/auth/refresh",
                             headers={"Cookie": "refresh_token=" + refresh0}, expected=401)
    actor, second_login = _login_attempt(Client, client.base, employee_no, password1)
    with conn.cursor() as cursor:
        cursor.execute("SELECT password_hash,must_change_password,status FROM users WHERE id=%s", (user_id,))
        password1_hash, must_change, user_status = cursor.fetchone()
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id=%s AND revoked=0", (user_id,))
        active_after_login = cursor.fetchone()[0]
    check("identity first-password flow persists the new credential and revokes the old session",
          first_login["mustChangePassword"] is True and forced["code"] == 40303
          and second_login["mustChangePassword"] is False
          and password1_hash != initial_hash and must_change == 0 and user_status == "ACTIVE"
          and active_after_login == 1)

    # A rejected ordinary change must leave both the password hash and live session intact.
    password2 = _password()
    wrong_old = _password()
    wrong_change = actor.call("PUT", "/api/v1/auth/password", {
        "oldPassword": wrong_old,
        "newPassword": password2,
    }, expected=400)
    surviving_profile = actor.call("GET", "/api/v1/auth/profile")
    with conn.cursor() as cursor:
        cursor.execute("SELECT password_hash,must_change_password FROM users WHERE id=%s", (user_id,))
        hash_after_rejection, flag_after_rejection = cursor.fetchone()
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id=%s AND revoked=0", (user_id,))
        sessions_after_rejection = cursor.fetchone()[0]
    check("identity wrong old password is atomic and preserves the valid session",
          wrong_change["code"] == 40001 and surviving_profile["user"]["id"] == user_id
          and hash_after_rejection == password1_hash and flag_after_rejection == 0
          and sessions_after_rejection == 1)

    # The normal second change has the same all-session revocation contract as the first.
    refresh1 = _refresh_cookie(actor)
    actor.call("PUT", "/api/v1/auth/password", {
        "oldPassword": password1,
        "newPassword": password2,
    })
    actor.call("GET", "/api/v1/auth/profile", expected=401)
    Client(client.base).call("POST", "/api/v1/auth/refresh",
                             headers={"Cookie": "refresh_token=" + refresh1}, expected=401)
    _, previous_password_result = _login_attempt(Client, client.base, employee_no, password1, expected=401)
    actor, normal_login = _login_attempt(Client, client.base, employee_no, password2)
    with conn.cursor() as cursor:
        cursor.execute("SELECT password_hash,must_change_password FROM users WHERE id=%s", (user_id,))
        password2_hash, flag_after_second_change = cursor.fetchone()
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id=%s AND revoked=0", (user_id,))
        active_after_second_login = cursor.fetchone()[0]
        cursor.execute("SELECT employee_no,detail FROM audit_logs WHERE user_id=%s AND action='PASSWORD_CHANGE' ORDER BY id", (user_id,))
        self_change_logs = cursor.fetchall()
    self_change_details = [json.loads(row[1]) for row in self_change_logs]
    check("identity ordinary password change invalidates the previous credential and session",
          previous_password_result["code"] == 40101 and normal_login["mustChangePassword"] is False
          and password2_hash != password1_hash and flag_after_second_change == 0
          and active_after_second_login == 1 and len(self_change_logs) == 2
          and all(row[0] == employee_no for row in self_change_logs)
          and all(detail.get("auditContext", {}).get("source") == "HTTP"
                  for detail in self_change_details)
          and all(secret not in " ".join(row[1] for row in self_change_logs)
                  for secret in (password0, password1, password2)))

    # An administrator reset and disable must revoke an already active internal session.
    refresh2 = _refresh_cookie(actor)
    password3 = _password()
    client.call("PUT", f"/api/v1/admin/users/{user_id}/password", {"newPassword": password3})
    actor.call("GET", "/api/v1/auth/profile", expected=401)
    Client(client.base).call("POST", "/api/v1/auth/refresh",
                             headers={"Cookie": "refresh_token=" + refresh2}, expected=401)
    _, reset_old_password = _login_attempt(Client, client.base, employee_no, password2, expected=401)
    with conn.cursor() as cursor:
        cursor.execute("SELECT password_hash,must_change_password,status FROM users WHERE id=%s", (user_id,))
        reset_hash, reset_flag, reset_status = cursor.fetchone()
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id=%s AND revoked=0", (user_id,))
        reset_sessions = cursor.fetchone()[0]
    reset_actor, reset_login = _login_attempt(Client, client.base, employee_no, password3)
    reset_refresh = _refresh_cookie(reset_actor)
    client.call("PUT", f"/api/v1/admin/users/{user_id}/status", {"status": "DISABLED"})
    reset_actor.call("GET", "/api/v1/auth/profile", expected=401)
    Client(client.base).call("POST", "/api/v1/auth/refresh",
                             headers={"Cookie": "refresh_token=" + reset_refresh}, expected=401)
    _, disabled_login = _login_attempt(Client, client.base, employee_no, password3, expected=401)
    with conn.cursor() as cursor:
        cursor.execute("SELECT status FROM users WHERE id=%s", (user_id,))
        disabled_persisted_status = cursor.fetchone()[0]
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id=%s AND revoked=0", (user_id,))
        disabled_sessions = cursor.fetchone()[0]
    client.call("PUT", f"/api/v1/admin/users/{user_id}/status", {"status": "ACTIVE"})
    reset_actor.call("GET", "/api/v1/auth/profile", expected=401)
    with conn.cursor() as cursor:
        cursor.execute("SELECT must_change_password,status FROM users WHERE id=%s", (user_id,))
        reenabled_flag, reenabled_status = cursor.fetchone()
        cursor.execute("SELECT COUNT(*) FROM refresh_tokens WHERE user_id=%s AND revoked=0", (user_id,))
        reenabled_sessions = cursor.fetchone()[0]
    reenabled_actor, reenabled_login = _login_attempt(Client, client.base, employee_no, password3)
    check("identity admin reset and disable persist state and never resurrect revoked sessions",
          reset_old_password["code"] == 40101 and reset_login["mustChangePassword"] is True
          and reset_hash != password2_hash and reset_flag == 1 and reset_status == "ACTIVE" and reset_sessions == 0
          and disabled_login["code"] == 40101 and reenabled_login["mustChangePassword"] is True
          and disabled_persisted_status == "DISABLED" and disabled_sessions == 0
          and reenabled_flag == 1 and reenabled_status == "ACTIVE" and reenabled_sessions == 0)

    # Finish the reset-password flow, then prove permission changes affect the same access token.
    password4 = _password()
    reenabled_actor.call("PUT", "/api/v1/auth/password", {
        "oldPassword": password3,
        "newPassword": password4,
    })
    actor, final_login = _login_attempt(Client, client.base, employee_no, password4)
    actor.call("GET", "/api/v1/admin/users")
    client.call("PUT", f"/api/v1/admin/roles/{role_id}/permissions", {"permissionIds": []})
    denied_after_revoke = actor.call("GET", "/api/v1/admin/users", expected=403)
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM role_permissions WHERE role_id=%s", (role_id,))
        permissions_after_revoke = cursor.fetchone()[0]
    client.call("PUT", f"/api/v1/admin/roles/{role_id}/permissions", {
        "permissionIds": [user_manage],
    })
    restored_page = actor.call("GET", "/api/v1/admin/users?" + urllib.parse.urlencode({
        "keyword": employee_no,
    }))
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM role_permissions WHERE role_id=%s AND permission_id=%s",
                       (role_id, user_manage))
        permissions_after_restore = cursor.fetchone()[0]
    check("identity live RBAC revocation and restore affect the same access token",
          final_login["mustChangePassword"] is False and denied_after_revoke["code"] == 40301
          and permissions_after_revoke == 0 and permissions_after_restore == 1
          and restored_page["total"] == 1 and restored_page["list"][0]["id"] == user_id)

    # Role status and deletion constraints must preserve both role and user state on rejection.
    disable_bound = client.call("PUT", f"/api/v1/admin/roles/{role_id}/status",
                                {"status": "DISABLED"}, expected=400)
    with conn.cursor() as cursor:
        cursor.execute("SELECT status FROM roles WHERE id=%s", (role_id,))
        status_after_bound_rejection = cursor.fetchone()[0]
    client.call("PUT", f"/api/v1/admin/users/{user_id}/status", {"status": "DISABLED"})
    client.call("PUT", f"/api/v1/admin/roles/{role_id}/status", {"status": "DISABLED"})
    enable_with_disabled_role = client.call("PUT", f"/api/v1/admin/users/{user_id}/status",
                                            {"status": "ACTIVE"}, expected=400)
    with conn.cursor() as cursor:
        cursor.execute("SELECT status FROM roles WHERE id=%s", (role_id,))
        disabled_role_status = cursor.fetchone()[0]
        cursor.execute("SELECT status FROM users WHERE id=%s", (user_id,))
        disabled_user_status = cursor.fetchone()[0]
    client.call("PUT", f"/api/v1/admin/roles/{role_id}/status", {"status": "ACTIVE"})
    client.call("PUT", f"/api/v1/admin/users/{user_id}/status", {"status": "ACTIVE"})
    delete_bound = client.call("DELETE", f"/api/v1/admin/roles/{role_id}", expected=400)
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM roles WHERE id=%s", (role_id,))
        role_after_delete_rejection = cursor.fetchone()[0]
    client.call("PUT", f"/api/v1/admin/users/{user_id}/roles", {
        "roleIds": [ordinary_role["id"]],
    })
    client.call("DELETE", f"/api/v1/admin/roles/{role_id}")
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM roles WHERE id=%s", (role_id,))
        role_after_delete = cursor.fetchone()[0]
        cursor.execute("SELECT role_id FROM user_roles WHERE user_id=%s", (user_id,))
        final_user_role = cursor.fetchone()[0]
    check("identity role binding status and delete invariants preserve persisted state",
          disable_bound["code"] == 40001 and status_after_bound_rejection == "ACTIVE"
          and enable_with_disabled_role["code"] == 40001
          and disabled_role_status == "DISABLED" and disabled_user_status == "DISABLED"
          and delete_bound["code"] == 40001 and role_after_delete_rejection == 1
          and role_after_delete == 0 and final_user_role == ordinary_role["id"])

    # Verify the management audit survives deletion and records actor, target, and deltas.
    role_logs = client.call("GET", "/api/v1/admin/audit-logs?" + urllib.parse.urlencode({
        "targetType": "role",
        "targetId": role_id,
        "pageSize": 100,
    }))
    role_rows = role_logs["list"]
    role_actions = {row["action"] for row in role_rows}
    permission_deltas = {
        (row["detail"]["oldPermissionCount"], row["detail"]["newPermissionCount"])
        for row in role_rows if row["action"] == "ROLE_ASSIGN_PERMS"
    }
    with conn.cursor() as cursor:
        cursor.execute("SELECT action,employee_no,detail FROM audit_logs WHERE target_type='user' AND target_id=%s AND action IN ('USER_RESET_PASSWORD','USER_STATUS') ORDER BY id", (str(user_id),))
        user_security_logs = cursor.fetchall()
    reset_details = [json.loads(row[2]) for row in user_security_logs if row[0] == "USER_RESET_PASSWORD"]
    status_details = [json.loads(row[2]) for row in user_security_logs if row[0] == "USER_STATUS"]
    serialized_security_details = " ".join(row[2] or "" for row in user_security_logs)
    check("identity lifecycle audit preserves actor target and security deltas without passwords",
          {"ROLE_CREATE", "ROLE_ASSIGN_PERMS", "ROLE_STATUS", "ROLE_DELETE"}.issubset(role_actions)
          and all(row["targetId"] == str(role_id) and row["userId"] == admin_profile["id"]
                  and row["employeeNo"] == admin_profile["employeeNo"] for row in role_rows)
          and {(0, 1), (1, 0)}.issubset(permission_deltas)
          and len(reset_details) == 1 and reset_details[0]["sessionsRevoked"] is True
          and reset_details[0]["mustChangePassword"] is True
          and {(item["oldStatus"], item["newStatus"]) for item in status_details}
          >= {("ACTIVE", "DISABLED"), ("DISABLED", "ACTIVE")}
          and all(row[1] == admin_profile["employeeNo"] for row in user_security_logs)
          and all(password not in serialized_security_details
                  for password in (password0, password1, password2, password3, password4)))
