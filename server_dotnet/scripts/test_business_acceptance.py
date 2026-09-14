"""End-to-end business acceptance checks using caller-owned isolated resources.

The caller supplies an authenticated system administrator, a disposable database,
and an API Client type. The API worker is disabled by the caller, so notification
checks stop at durable PENDING outbox rows and never deliver email.
"""

import hashlib
import secrets
import urllib.parse


def _password():
    return "Yf9!" + secrets.token_urlsafe(9)


def _flatten_departments(nodes):
    result = []
    for node in nodes:
        result.append(node)
        result.extend(_flatten_departments(node.get("children") or []))
    return result


def _activate_user(admin_client, Client, check, employee_no, initial_password, user_id, label):
    actor = Client(admin_client.base)
    first = actor.login(employee_no, initial_password)
    actor.call("GET", "/api/v1/projects", expected=403)
    changed_password = _password()
    actor.call("PUT", "/api/v1/auth/password", {
        "oldPassword": initial_password,
        "newPassword": changed_password,
    })
    actor.call("GET", "/api/v1/auth/profile", expected=401)
    second = actor.login(employee_no, changed_password)
    profile = actor.call("GET", "/api/v1/auth/profile")
    check(
        label + " completes the real first-password flow",
        first["mustChangePassword"] is True
        and second["mustChangePassword"] is False
        and profile["user"]["id"] == user_id,
    )
    return actor


def _upload_chunks(actor, project_id, file_name, content):
    initialized = actor.call("POST", "/api/v1/uploads/init", {
        "projectId": project_id,
        "fileName": file_name,
        "fileSize": len(content),
        "fileMd5": hashlib.md5(content).hexdigest(),
    })
    chunk_size = initialized["chunkSize"]
    for index in range(initialized["totalChunks"]):
        actor.call(
            "PUT",
            f"/api/v1/uploads/{initialized['sessionId']}/chunks/{index}",
            content[index * chunk_size:(index + 1) * chunk_size],
            headers={"Content-Type": "application/octet-stream"},
        )
    state = actor.call("GET", f"/api/v1/uploads/{initialized['sessionId']}")
    merged = actor.call("POST", f"/api/v1/uploads/{initialized['sessionId']}/merge")
    file_id = merged.get("id", merged.get("fileId"))
    if file_id is None:
        raise AssertionError("business upload merge response missing file identifier")
    return file_id, initialized["totalChunks"], state["uploadedChunks"]


def _download_matches(actor, file_id, expected):
    downloaded, _ = actor.call("GET", f"/api/v1/files/{file_id}/download", raw=True)
    return hashlib.sha256(downloaded).digest() == hashlib.sha256(expected).digest()


def _audit_actions(admin_client, target_type, target_id):
    query = urllib.parse.urlencode({
        "targetType": target_type,
        "targetId": target_id,
        "pageSize": 100,
    })
    page = admin_client.call("GET", "/api/v1/admin/audit-logs?" + query)
    return {item["action"] for item in page["list"]}


def _exchange_messages(internal_client, supplier_client, project_id, internal_id, supplier_id, label, check):
    internal_message = internal_client.call("POST", f"/api/v1/projects/{project_id}/messages", {
        "content": label + "：公司发给供应商",
    })
    supplier_page = supplier_client.call(
        "GET", f"/api/v1/projects/{project_id}/messages?targetId={internal_message['id']}")
    supplier_client.call("POST", "/api/v1/messages/read", {"ids": [internal_message["id"]]})
    internal_receipt = supplier_client.call("GET", f"/api/v1/messages/{internal_message['id']}/reads")

    supplier_message = supplier_client.call("POST", f"/api/v1/projects/{project_id}/messages", {
        "content": label + "：供应商回复公司",
    })
    internal_page = internal_client.call(
        "GET", f"/api/v1/projects/{project_id}/messages?targetId={supplier_message['id']}")
    internal_client.call("POST", "/api/v1/messages/read", {"ids": [supplier_message["id"]]})
    supplier_receipt = internal_client.call("GET", f"/api/v1/messages/{supplier_message['id']}/reads")
    check(
        label + " exchanges messages and persists read receipts",
        supplier_page["list"][0]["readByMe"] is False
        and internal_page["list"][0]["readByMe"] is False
        and any(row["userId"] == supplier_id for row in internal_receipt["readers"])
        and any(row["userId"] == internal_id for row in supplier_receipt["readers"]),
    )
    return internal_message["id"], supplier_message["id"]


def _create_started_project(admin_client, supplier_id, internal_id, name):
    project = admin_client.call("POST", "/api/v1/projects", {
        "name": name,
        "description": "owned isolated full business acceptance fixture",
        "supplierId": supplier_id,
    })
    admin_client.call("PUT", f"/api/v1/projects/{project['id']}/members", {
        "userIds": [internal_id],
    })
    admin_client.call("PUT", f"/api/v1/projects/{project['id']}/status", {
        "status": "IN_PROGRESS",
    })
    return project["id"]


def run_business_acceptance(client, Client, conn, check):
    suffix = secrets.token_hex(4)
    permissions = client.call("GET", "/api/v1/permissions")
    permission_ids = {item["code"]: item["id"] for item in permissions}
    with conn.cursor() as cursor:
        cursor.execute("SELECT id FROM users WHERE employee_no='admin'")
        admin_user_id = cursor.fetchone()[0]

    # Complete CRUD uses disposable objects with no login or business history, so
    # their delete paths can be verified without weakening retention invariants.
    division = client.call("POST", "/api/v1/admin/departments", {
        "name": "验收临时事业部-" + suffix, "parentId": None, "sortNo": 80,
    })
    department = client.call("POST", "/api/v1/admin/departments", {
        "name": "验收临时部门-" + suffix, "parentId": division["id"], "sortNo": 81,
    })
    department = client.call("PUT", f"/api/v1/admin/departments/{department['id']}", {
        "name": "验收临时部门更新-" + suffix, "parentId": division["id"], "sortNo": 82,
    })
    client.call("PUT", f"/api/v1/admin/departments/{department['id']}/status", {"status": "DISABLED"})
    client.call("PUT", f"/api/v1/admin/departments/{department['id']}/status", {"status": "ACTIVE"})

    role_one = client.call("POST", "/api/v1/admin/roles", {
        "name": "验收临时角色A-" + suffix, "description": "before update",
    })
    role_two = client.call("POST", "/api/v1/admin/roles", {
        "name": "验收临时角色B-" + suffix, "description": "assignment target",
    })
    client.call("PUT", f"/api/v1/admin/roles/{role_one['id']}/permissions", {
        "permissionIds": [permission_ids["dashboard"], permission_ids["project:list"]],
    })
    role_one = client.call("PUT", f"/api/v1/admin/roles/{role_one['id']}", {
        "name": "验收临时角色A更新-" + suffix, "description": "after update",
    })
    client.call("PUT", f"/api/v1/admin/roles/{role_one['id']}/status", {"status": "DISABLED"})
    client.call("PUT", f"/api/v1/admin/roles/{role_one['id']}/status", {"status": "ACTIVE"})

    disposable_employee = "ba_crud_" + suffix
    disposable_password = _password()
    disposable_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": disposable_employee,
        "password": disposable_password,
        "realName": "验收临时员工",
        "email": disposable_employee + "@example.invalid",
        "departmentId": department["id"],
        "roleId": role_one["id"],
    })
    disposable_user = client.call("PUT", f"/api/v1/admin/users/{disposable_user['id']}", {
        "realName": "验收临时员工更新",
        "email": "updated." + disposable_employee + "@example.invalid",
        "departmentId": department["id"],
        "roleId": role_one["id"],
    })
    client.call("PUT", f"/api/v1/admin/users/{disposable_user['id']}/status", {"status": "DISABLED"})
    client.call("PUT", f"/api/v1/admin/users/{disposable_user['id']}/status", {"status": "ACTIVE"})
    client.call("PUT", f"/api/v1/admin/users/{disposable_user['id']}/password", {
        "newPassword": _password(),
    })
    client.call("PUT", f"/api/v1/admin/users/{disposable_user['id']}/roles", {
        "roleIds": [role_two["id"]],
    })
    user_page = client.call("GET", "/api/v1/admin/users?" + urllib.parse.urlencode({
        "keyword": disposable_employee, "pageSize": 100,
    }))
    check(
        "organization role and internal-user CRUD updates are readable",
        user_page["total"] == 1
        and user_page["list"][0]["realName"] == "验收临时员工更新"
        and user_page["list"][0]["roleId"] == role_two["id"],
    )
    disposable_user_id = disposable_user["id"]
    client.call("DELETE", f"/api/v1/admin/users/{disposable_user_id}")
    client.call("DELETE", f"/api/v1/admin/roles/{role_one['id']}")
    client.call("DELETE", f"/api/v1/admin/roles/{role_two['id']}")
    client.call("DELETE", f"/api/v1/admin/departments/{department['id']}")
    client.call("DELETE", f"/api/v1/admin/departments/{division['id']}")
    role_page = client.call("GET", "/api/v1/admin/roles?pageSize=100")
    department_ids = {item["id"] for item in _flatten_departments(client.call("GET", "/api/v1/departments"))}
    check(
        "organization role and internal-user CRUD deletes are effective",
        all(item["id"] not in {role_one["id"], role_two["id"]} for item in role_page["list"])
        and division["id"] not in department_ids
        and department["id"] not in department_ids
        and client.call("GET", "/api/v1/admin/users?" + urllib.parse.urlencode({
            "keyword": disposable_employee,
        }))["total"] == 0,
    )

    disposable_supplier = client.call("POST", "/api/v1/admin/suppliers", {
        "name": "验收临时供应商-" + suffix, "remark": "before update",
    })
    disposable_supplier = client.call("PUT", f"/api/v1/admin/suppliers/{disposable_supplier['id']}", {
        "name": "验收临时供应商更新-" + suffix, "remark": "after update",
    })
    client.call("PUT", f"/api/v1/admin/suppliers/{disposable_supplier['id']}/status", {"status": "DISABLED"})
    client.call("PUT", f"/api/v1/admin/suppliers/{disposable_supplier['id']}/status", {"status": "ACTIVE"})
    disposable_supplier_employee = "ba_scrud_" + suffix
    disposable_account = client.call(
        "POST", f"/api/v1/admin/suppliers/{disposable_supplier['id']}/accounts", {
            "employeeNo": disposable_supplier_employee,
            "password": _password(),
            "realName": "验收临时供应商账号",
            "email": disposable_supplier_employee + "@example.invalid",
        })
    disposable_account = client.call("PUT", f"/api/v1/admin/supplier-accounts/{disposable_account['id']}", {
        "realName": "验收临时供应商账号更新",
        "email": "updated." + disposable_supplier_employee + "@example.invalid",
    })
    client.call("PUT", f"/api/v1/admin/supplier-accounts/{disposable_account['id']}/status", {
        "status": "DISABLED",
    })
    client.call("PUT", f"/api/v1/admin/supplier-accounts/{disposable_account['id']}/status", {
        "status": "ACTIVE",
    })
    client.call("PUT", f"/api/v1/admin/supplier-accounts/{disposable_account['id']}/password", {
        "newPassword": _password(),
    })
    accounts = client.call("GET", f"/api/v1/admin/suppliers/{disposable_supplier['id']}/accounts")
    detail = client.call("GET", f"/api/v1/admin/suppliers/{disposable_supplier['id']}")
    check(
        "supplier and supplier-account CRUD updates are readable",
        detail["name"] == disposable_supplier["name"]
        and len(accounts) == 1
        and accounts[0]["realName"] == disposable_account["realName"],
    )
    disposable_supplier_id = disposable_supplier["id"]
    disposable_account_id = disposable_account["id"]
    client.call("DELETE", f"/api/v1/admin/supplier-accounts/{disposable_account_id}")
    client.call("DELETE", f"/api/v1/admin/suppliers/{disposable_supplier_id}")
    supplier_page = client.call("GET", "/api/v1/admin/suppliers?" + urllib.parse.urlencode({
        "keyword": "验收临时供应商更新-" + suffix,
    }))
    check("supplier and supplier-account CRUD deletes are effective", supplier_page["total"] == 0)

    # Create persistent business actors. Every account follows the same public
    # first-password flow used by the browser; no database flag is patched.
    business_division = client.call("POST", "/api/v1/admin/departments", {
        "name": "验收业务事业部-" + suffix, "parentId": None, "sortNo": 90,
    })
    business_department = client.call("POST", "/api/v1/admin/departments", {
        "name": "验收业务部门-" + suffix, "parentId": business_division["id"], "sortNo": 91,
    })
    role_options = client.call("GET", "/api/v1/admin/user-role-options")
    internal_role = next(role for role in role_options if role["name"] == "内部成员")
    internal_employee = "ba_internal_" + suffix
    internal_initial = _password()
    internal_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": internal_employee,
        "password": internal_initial,
        "realName": "验收内部成员",
        "email": internal_employee + "@example.invalid",
        "departmentId": business_department["id"],
        "roleId": internal_role["id"],
    })
    internal_client = _activate_user(
        client, Client, check, internal_employee, internal_initial, internal_user["id"], "internal member")

    business_suppliers = []
    supplier_clients = []
    for code, label in (("a", "甲"), ("b", "乙")):
        supplier = client.call("POST", "/api/v1/admin/suppliers", {
            "name": f"验收供应商{label}-" + suffix,
            "remark": "owned isolated business participant",
        })
        employee = f"ba_sup{code}_" + suffix
        initial_password = _password()
        account = client.call("POST", f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
            "employeeNo": employee,
            "password": initial_password,
            "realName": f"验收供应商{label}账号",
            "email": employee + "@example.invalid",
        })
        supplier_client = _activate_user(
            client, Client, check, employee, initial_password, account["id"], f"supplier {code.upper()}")
        business_suppliers.append((supplier, account))
        supplier_clients.append(supplier_client)

    supplier_a, account_a = business_suppliers[0]
    supplier_b, account_b = business_suppliers[1]
    supplier_a_client, supplier_b_client = supplier_clients
    project_a = _create_started_project(
        client, supplier_a["id"], internal_user["id"], "验收甲项目-" + suffix)
    project_b = _create_started_project(
        client, supplier_b["id"], internal_user["id"], "验收乙项目-" + suffix)

    internal_a_bytes = (b"company-to-supplier-a\n" * 16000) + b"EOF-A"
    supplier_a_bytes = (b"supplier-a-to-company\n" * 16000) + b"EOF-SA"
    internal_b_bytes = (b"company-to-supplier-b\n" * 16000) + b"EOF-B"
    supplier_b_bytes = (b"supplier-b-to-company\n" * 16000) + b"EOF-SB"
    file_a_company, chunks_a_company, uploaded_a_company = _upload_chunks(
        internal_client, project_a, "company-a.zip", internal_a_bytes)
    file_a_supplier, chunks_a_supplier, uploaded_a_supplier = _upload_chunks(
        supplier_a_client, project_a, "supplier-a.zip", supplier_a_bytes)
    file_b_company, chunks_b_company, uploaded_b_company = _upload_chunks(
        internal_client, project_b, "company-b.zip", internal_b_bytes)
    file_b_supplier, chunks_b_supplier, uploaded_b_supplier = _upload_chunks(
        supplier_b_client, project_b, "supplier-b.zip", supplier_b_bytes)
    files_a = supplier_a_client.call("GET", f"/api/v1/projects/{project_a}/files?pageSize=100")
    files_b = supplier_b_client.call("GET", f"/api/v1/projects/{project_b}/files?pageSize=100")
    directions_a = {item["id"]: item["direction"] for item in files_a["list"]}
    directions_b = {item["id"]: item["direction"] for item in files_b["list"]}
    check(
        "both suppliers exchange real multi-chunk files with exact SHA256",
        min(chunks_a_company, chunks_a_supplier, chunks_b_company, chunks_b_supplier) > 1
        and all(len(uploaded) == chunks for uploaded, chunks in (
            (uploaded_a_company, chunks_a_company),
            (uploaded_a_supplier, chunks_a_supplier),
            (uploaded_b_company, chunks_b_company),
            (uploaded_b_supplier, chunks_b_supplier),
        ))
        and directions_a[file_a_company] == "C2S"
        and directions_a[file_a_supplier] == "S2C"
        and directions_b[file_b_company] == "C2S"
        and directions_b[file_b_supplier] == "S2C"
        and _download_matches(supplier_a_client, file_a_company, internal_a_bytes)
        and _download_matches(internal_client, file_a_supplier, supplier_a_bytes)
        and _download_matches(supplier_b_client, file_b_company, internal_b_bytes)
        and _download_matches(internal_client, file_b_supplier, supplier_b_bytes),
    )

    message_a = _exchange_messages(
        internal_client, supplier_a_client, project_a, internal_user["id"], account_a["id"],
        "供应商甲项目", check)
    message_b = _exchange_messages(
        internal_client, supplier_b_client, project_b, internal_user["id"], account_b["id"],
        "供应商乙项目", check)

    project_ids_a = {item["id"] for item in supplier_a_client.call(
        "GET", "/api/v1/projects?pageSize=100")["list"]}
    project_ids_b = {item["id"] for item in supplier_b_client.call(
        "GET", "/api/v1/projects?pageSize=100")["list"]}
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT "
            "(SELECT COUNT(*) FROM upload_sessions WHERE "
            " (project_id=%s AND uploader_id=%s) OR (project_id=%s AND uploader_id=%s)),"
            "(SELECT COUNT(*) FROM messages WHERE "
            " (project_id=%s AND sender_id=%s) OR (project_id=%s AND sender_id=%s)),"
            "(SELECT COUNT(*) FROM audit_logs WHERE user_id IN (%s,%s) "
            " AND action IN ('FILE_UPLOAD','FILE_BATCH_DOWNLOAD','MESSAGE_CREATE')) ,"
            "(SELECT COUNT(*) FROM email_outbox WHERE project_id IN (%s,%s))",
            (
                project_a, account_b["id"], project_b, account_a["id"],
                project_a, account_b["id"], project_b, account_a["id"],
                account_a["id"], account_b["id"], project_a, project_b,
            ),
        )
        foreign_write_state_before = cursor.fetchone()

    supplier_b_client.call("GET", f"/api/v1/projects/{project_a}", expected=403)
    supplier_b_client.call("GET", f"/api/v1/projects/{project_a}/messages", expected=403)
    supplier_b_client.call("GET", f"/api/v1/projects/{project_a}/files", expected=403)
    supplier_b_client.call("GET", f"/api/v1/messages/{message_a[0]}/reads", expected=403)
    supplier_b_client.call("GET", f"/api/v1/files/{file_a_company}/content", expected=403, raw=True)
    supplier_b_client.call("GET", f"/api/v1/files/{file_a_company}/download", expected=403, raw=True)
    supplier_b_client.call("POST", "/api/v1/files/batch-download", {
        "ids": [file_a_company, file_a_supplier],
    }, expected=403)
    foreign_b_bytes = b"supplier-b-must-not-upload-to-company-a"
    supplier_b_client.call("POST", "/api/v1/uploads/init", {
        "projectId": project_a,
        "fileName": "foreign-b-to-a-" + suffix + ".zip",
        "fileSize": len(foreign_b_bytes),
        "fileMd5": hashlib.md5(foreign_b_bytes).hexdigest(),
    }, expected=403)
    supplier_b_client.call("POST", f"/api/v1/projects/{project_a}/messages", {
        "content": "供应商乙不得写入供应商甲项目",
    }, expected=403)

    supplier_a_client.call("GET", f"/api/v1/projects/{project_b}", expected=403)
    supplier_a_client.call("GET", f"/api/v1/projects/{project_b}/messages", expected=403)
    supplier_a_client.call("GET", f"/api/v1/projects/{project_b}/files", expected=403)
    supplier_a_client.call("GET", f"/api/v1/messages/{message_b[0]}/reads", expected=403)
    supplier_a_client.call("GET", f"/api/v1/files/{file_b_supplier}/content", expected=403, raw=True)
    supplier_a_client.call("GET", f"/api/v1/files/{file_b_supplier}/download", expected=403, raw=True)
    supplier_a_client.call("POST", "/api/v1/files/batch-download", {
        "ids": [file_b_company, file_b_supplier],
    }, expected=403)
    foreign_a_bytes = b"supplier-a-must-not-upload-to-company-b"
    supplier_a_client.call("POST", "/api/v1/uploads/init", {
        "projectId": project_b,
        "fileName": "foreign-a-to-b-" + suffix + ".zip",
        "fileSize": len(foreign_a_bytes),
        "fileMd5": hashlib.md5(foreign_a_bytes).hexdigest(),
    }, expected=403)
    supplier_a_client.call("POST", f"/api/v1/projects/{project_b}/messages", {
        "content": "供应商甲不得写入供应商乙项目",
    }, expected=403)

    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT "
            "(SELECT COUNT(*) FROM upload_sessions WHERE "
            " (project_id=%s AND uploader_id=%s) OR (project_id=%s AND uploader_id=%s)),"
            "(SELECT COUNT(*) FROM messages WHERE "
            " (project_id=%s AND sender_id=%s) OR (project_id=%s AND sender_id=%s)),"
            "(SELECT COUNT(*) FROM audit_logs WHERE user_id IN (%s,%s) "
            " AND action IN ('FILE_UPLOAD','FILE_BATCH_DOWNLOAD','MESSAGE_CREATE')) ,"
            "(SELECT COUNT(*) FROM email_outbox WHERE project_id IN (%s,%s))",
            (
                project_a, account_b["id"], project_b, account_a["id"],
                project_a, account_b["id"], project_b, account_a["id"],
                account_a["id"], account_b["id"], project_a, project_b,
            ),
        )
        foreign_write_state_after = cursor.fetchone()
    check(
        "supplier project file upload content download batch and message data is isolated both ways",
        project_a in project_ids_a and project_b not in project_ids_a
        and project_b in project_ids_b and project_a not in project_ids_b
        and foreign_write_state_after == foreign_write_state_before,
    )

    project_a_submission = internal_client.call(
        "POST", f"/api/v1/projects/{project_a}/submit", {})
    client.call("POST", f"/api/v1/projects/{project_a}/confirm")
    project_b_submission = supplier_b_client.call("POST", f"/api/v1/projects/{project_b}/submit", {
        "confirmSide": "COMPANY",
    })
    internal_client.call("POST", f"/api/v1/projects/{project_b}/confirm")
    project_a_detail = internal_client.call("GET", f"/api/v1/projects/{project_a}")
    project_b_detail = internal_client.call("GET", f"/api/v1/projects/{project_b}")
    activities_a = internal_client.call("GET", f"/api/v1/projects/{project_a}/activities?pageSize=50")
    activities_b = internal_client.call("GET", f"/api/v1/projects/{project_b}/activities?pageSize=50")
    actions_a = {item["action"] for item in activities_a["list"]}
    actions_b = {item["action"] for item in activities_b["list"]}
    check(
        "internal and supplier submissions both complete through internal acceptance",
        project_a_submission["confirmSide"] == "COMPANY"
        and project_b_submission["confirmSide"] == "COMPANY"
        and project_a_detail["status"] == "COMPLETED"
        and project_b_detail["status"] == "COMPLETED"
        and activities_a["summary"]["status"] == "COMPLETED"
        and activities_b["summary"]["status"] == "COMPLETED"
        and {"START", "SUBMIT", "CONFIRM", "UPLOAD", "CREATE"}.issubset(actions_a)
        and {"START", "SUBMIT", "CONFIRM", "UPLOAD", "CREATE"}.issubset(actions_b),
    )

    # Verify audit visibility through the API after the target rows have been
    # deleted, and independently verify that notifications remain durable.
    check(
        "full CRUD and collaboration actions remain in the audit API",
        {"DEPT_CREATE", "DEPT_UPDATE", "DEPT_STATUS", "DEPT_DELETE"}.issubset(
            _audit_actions(client, "department", department["id"]))
        and {"ROLE_CREATE", "ROLE_UPDATE", "ROLE_STATUS", "ROLE_ASSIGN_PERMS", "ROLE_DELETE"}.issubset(
            _audit_actions(client, "role", role_one["id"]))
        and {
            "USER_CREATE", "USER_UPDATE", "USER_STATUS", "USER_RESET_PASSWORD",
            "USER_ASSIGN_ROLE", "USER_DELETE",
        }.issubset(_audit_actions(client, "user", disposable_user_id))
        and {"SUPPLIER_CREATE", "SUPPLIER_UPDATE", "SUPPLIER_STATUS", "SUPPLIER_DELETE"}.issubset(
            _audit_actions(client, "supplier", disposable_supplier_id))
        and {"SUPPLIER_ACCOUNT_CREATE", "SUPPLIER_ACCOUNT_UPDATE", "SUPPLIER_ACCOUNT_STATUS",
             "SUPPLIER_ACCOUNT_RESET_PASSWORD", "SUPPLIER_ACCOUNT_DELETE"}.issubset(
            _audit_actions(client, "user", disposable_account_id))
        and {"PROJECT_CREATE", "PROJECT_START", "PROJECT_MEMBERS", "PROJECT_SUBMIT", "PROJECT_CONFIRM"}.issubset(
            _audit_actions(client, "project", project_a))
        and {"FILE_UPLOAD", "FILE_DOWNLOAD"}.issubset(_audit_actions(client, "file", file_a_company))
        and {"MESSAGE_CREATE"}.issubset(_audit_actions(client, "message", message_a[0])),
    )
    for employee in (internal_employee, "ba_supa_" + suffix, "ba_supb_" + suffix):
        auth_page = client.call("GET", "/api/v1/admin/audit-logs?" + urllib.parse.urlencode({
            "category": "AUTH", "employeeNo": employee, "pageSize": 100,
        }))
        check(
            employee + " login and password-change audit is visible",
            {"LOGIN", "PASSWORD_CHANGE"}.issubset({item["action"] for item in auth_page["list"]}),
        )

    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT project_id,event_type,recipient_user_id,status,retry_count "
            "FROM email_outbox WHERE project_id IN (%s,%s) ORDER BY id",
            (project_a, project_b),
        )
        outbox = cursor.fetchall()
    rows_a = {(event_type, recipient_id) for pid, event_type, recipient_id, _, _ in outbox if pid == project_a}
    rows_b = {(event_type, recipient_id) for pid, event_type, recipient_id, _, _ in outbox if pid == project_b}
    check(
        "disabled mail worker leaves complete tenant-scoped notification outbox",
        len(outbox) > 0
        and all(status == "PENDING" and retries == 0 for _, _, _, status, retries in outbox)
        and {("FILE_UPLOADED", account_a["id"]),
             ("FILE_UPLOADED", internal_user["id"]),
             ("MESSAGE_CREATED", account_a["id"]),
             ("MESSAGE_CREATED", internal_user["id"]),
             ("PROJECT_SUBMITTED", admin_user_id),
             ("PROJECT_CONFIRMED", internal_user["id"])}.issubset(rows_a)
        and {("FILE_UPLOADED", account_b["id"]),
             ("FILE_UPLOADED", internal_user["id"]),
             ("MESSAGE_CREATED", account_b["id"]),
             ("MESSAGE_CREATED", internal_user["id"]),
             ("PROJECT_SUBMITTED", internal_user["id"]),
             ("PROJECT_CONFIRMED", account_b["id"])}.issubset(rows_b)
        and all(recipient_id != account_b["id"] for _, recipient_id in rows_a)
        and all(recipient_id != account_a["id"] for _, recipient_id in rows_b),
    )
