"""Persistent project-lifecycle acceptance using caller-owned isolated resources.

The caller supplies an authenticated administrator, disposable database connection,
and API Client type. The mail worker must remain disabled; notification assertions
stop at durable PENDING outbox rows.
"""

import secrets

from test_business_acceptance import _activate_user, _download_matches, _password, _upload_chunks


def _project_snapshot(conn, project_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT status,confirm_side,name,description FROM projects WHERE id=%s",
            (project_id,),
        )
        project = cursor.fetchone()
        cursor.execute(
            "SELECT action,operator_id,confirm_side,reason FROM project_status_logs "
            "WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        history = tuple(cursor.fetchall())
        cursor.execute(
            "SELECT event_type,recipient_user_id,status,retry_count FROM email_outbox "
            "WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        outbox = tuple(cursor.fetchall())
        cursor.execute(
            "SELECT user_id FROM project_members WHERE project_id=%s ORDER BY user_id",
            (project_id,),
        )
        members = tuple(row[0] for row in cursor.fetchall())
        cursor.execute(
            "SELECT id,status,deleted_at FROM files WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        files = tuple(cursor.fetchall())
        cursor.execute(
            "SELECT id,status,content FROM messages WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        messages = tuple(cursor.fetchall())
        cursor.execute(
            "SELECT id,status,result_file_id FROM upload_sessions WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        uploads = tuple(cursor.fetchall())
        cursor.execute(
            "SELECT action,target_type,target_id FROM audit_logs ORDER BY id"
        )
        audit = tuple(cursor.fetchall())
        cursor.execute(
            "SELECT activity_type,action,target_id FROM project_activities "
            "WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        activities = tuple(cursor.fetchall())
    return project, history, outbox, members, files, messages, uploads, audit, activities


def _expect_atomic_rejection(check, conn, actor, project_id, label, method, path,
                             body=None, expected=409):
    before = _project_snapshot(conn, project_id)
    actor.call(method, path, body, expected=expected)
    after = _project_snapshot(conn, project_id)
    check(label + " is rejected without state audit or outbox writes", after == before)


def _event_count(conn, project_id, event_type, recipient_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT COUNT(*) FROM email_outbox WHERE project_id=%s AND event_type=%s "
            "AND recipient_user_id=%s AND status='PENDING' AND retry_count=0",
            (project_id, event_type, recipient_id),
        )
        return cursor.fetchone()[0]


def _check_equal(check, label, actual, expected):
    if actual != expected:
        raise AssertionError(f"{label}: expected {expected!r}, got {actual!r}")
    check(label, True)


def _audit_actions(conn, project_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT action FROM audit_logs WHERE target_type='project' AND target_id=%s ORDER BY id",
            (str(project_id),),
        )
        return [row[0] for row in cursor.fetchall()]


def run_workflow_acceptance(client, Client, conn, check):
    suffix = secrets.token_hex(5)
    permissions = client.call("GET", "/api/v1/permissions")
    withdraw_permission_id = next(
        permission["id"] for permission in permissions
        if permission["code"] == "project:withdraw")
    confirm_permission_id = next(
        permission["id"] for permission in permissions
        if permission["code"] == "project:confirm")
    supplier_role = next(
        role for role in client.call("GET", "/api/v1/admin/roles?pageSize=100")["list"]
        if role["name"] == "供应商人员")
    original_supplier_permission_ids = list(supplier_role["permissionIds"])
    check(
        "default supplier role excludes confirmation and implicit withdrawal",
        confirm_permission_id not in original_supplier_permission_ids
        and withdraw_permission_id not in original_supplier_permission_ids,
    )
    supplier = client.call("POST", "/api/v1/admin/suppliers", {
        "name": "工作流完整验收供应商-" + suffix,
        "remark": "owned isolated workflow acceptance fixture",
    })
    supplier_employee = "wf_supplier_" + suffix
    supplier_initial = _password()
    supplier_user = client.call(
        "POST", f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
            "employeeNo": supplier_employee,
            "password": supplier_initial,
            "realName": "工作流验收供应商",
            "email": supplier_employee + "@example.invalid",
        })
    supplier_client = _activate_user(
        client, Client, check, supplier_employee, supplier_initial,
        supplier_user["id"], "workflow supplier")

    internal_role = next(
        role for role in client.call("GET", "/api/v1/admin/user-role-options")
        if role["name"] == "内部成员")
    internal_employee = "wf_internal_" + suffix
    internal_initial = _password()
    internal_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": internal_employee,
        "password": internal_initial,
        "realName": "工作流验收内部成员",
        "email": internal_employee + "@example.invalid",
        "departmentId": None,
        "roleId": internal_role["id"],
    })
    internal_client = _activate_user(
        client, Client, check, internal_employee, internal_initial,
        internal_user["id"], "workflow internal member")

    confirm_only_role = client.call("POST", "/api/v1/admin/roles", {
        "name": "仅确认无项目菜单-" + suffix,
        "description": "verifies notification recipients can actually open the project",
    })
    client.call("PUT", f"/api/v1/admin/roles/{confirm_only_role['id']}/permissions", {
        "permissionIds": [confirm_permission_id],
    })
    confirm_only_employee = "wf_confirm_only_" + suffix
    confirm_only_initial = _password()
    confirm_only_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": confirm_only_employee,
        "password": confirm_only_initial,
        "realName": "仅确认无项目菜单成员",
        "email": confirm_only_employee + "@example.invalid",
        "departmentId": None,
        "roleId": confirm_only_role["id"],
    })
    confirm_only_client = _activate_user(
        client, Client, check, confirm_only_employee, confirm_only_initial,
        confirm_only_user["id"], "workflow confirm-only member")
    confirm_only_client.call("GET", "/api/v1/projects", expected=403)

    created = client.call("POST", "/api/v1/projects", {
        "name": "工作流完整验收项目-" + suffix,
        "description": "draft lifecycle fixture",
        "supplierId": supplier["id"],
    })
    project_id = created["id"]
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT action,from_status,to_status FROM project_status_logs "
            "WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        draft_history = list(cursor.fetchall())
        cursor.execute(
            "SELECT COUNT(*) FROM email_outbox WHERE project_id=%s",
            (project_id,),
        )
        draft_outbox = cursor.fetchone()[0]
    check(
        "project creation persists an auditable DRAFT without notifications",
        created["status"] == "DRAFT"
        and created["confirmSide"] is None
        and draft_history == [("CREATE", None, "DRAFT")]
        and _audit_actions(conn, project_id) == ["PROJECT_CREATE"]
        and draft_outbox == 0,
    )

    client.call("PUT", f"/api/v1/projects/{project_id}/members", {
        "userIds": [internal_user["id"], confirm_only_user["id"]],
    })
    started = client.call("PUT", f"/api/v1/projects/{project_id}/status", {
        "status": "IN_PROGRESS",
    })
    outbox_before_management = _event_count(
        conn, project_id, "PROJECT_SUBMITTED", internal_user["id"])
    terminated = client.call("PUT", f"/api/v1/projects/{project_id}/status", {
        "status": "TERMINATED",
    })
    restarted = client.call("PUT", f"/api/v1/projects/{project_id}/status", {
        "status": "IN_PROGRESS",
    })
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT action,from_status,to_status FROM project_status_logs "
            "WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        management_history = list(cursor.fetchall())
        cursor.execute(
            "SELECT COUNT(*) FROM email_outbox WHERE project_id=%s",
            (project_id,),
        )
        management_outbox = cursor.fetchone()[0]
    project_audits = _audit_actions(conn, project_id)
    check(
        "terminate and restart persist exact state history and audit",
        started["status"] == "IN_PROGRESS"
        and terminated["status"] == "TERMINATED"
        and restarted["status"] == "IN_PROGRESS"
        and management_history == [
            ("CREATE", None, "DRAFT"),
            ("START", "DRAFT", "IN_PROGRESS"),
            ("TERMINATE", "IN_PROGRESS", "TERMINATED"),
            ("RESTART", "TERMINATED", "IN_PROGRESS"),
        ]
        and {"PROJECT_START", "PROJECT_TERMINATE", "PROJECT_RESTART"}.issubset(project_audits)
        and management_outbox == outbox_before_management == 0,
    )

    payload = (b"workflow-completed-read-boundary\n" * 12000) + b"EOF"
    file_id, _, _ = _upload_chunks(
        supplier_client, project_id, "workflow-boundary.pdf", payload)
    check(
        "file upload notifies a visible member but not a member without project:list",
        _event_count(conn, project_id, "FILE_UPLOADED", internal_user["id"]) == 1
        and _event_count(conn, project_id, "FILE_UPLOADED", confirm_only_user["id"]) == 0,
    )
    message = supplier_client.call(
        "POST", f"/api/v1/projects/{project_id}/messages",
        {"content": "完成前创建，完成后仍应可读但不可删除"})

    _expect_atomic_rejection(
        check, conn, internal_client, project_id,
        "internal explicit supplier-side project submission",
        "POST", f"/api/v1/projects/{project_id}/submit",
        {"confirmSide": "SUPPLIER"}, expected=400)
    _expect_atomic_rejection(
        check, conn, supplier_client, project_id,
        "supplier explicit supplier-side project submission",
        "POST", f"/api/v1/projects/{project_id}/submit",
        {"confirmSide": "SUPPLIER"}, expected=400)
    submitted_for_reject = supplier_client.call(
        "POST", f"/api/v1/projects/{project_id}/submit", {})
    with conn.cursor() as cursor:
        cursor.execute(
            "INSERT INTO role_permissions(role_id,permission_id) VALUES(%s,%s)",
            (supplier_role["id"], confirm_permission_id),
        )
    try:
        _expect_atomic_rejection(
            check, conn, supplier_client, project_id,
            "supplier confirmation with a legacy project:confirm grant",
            "POST", f"/api/v1/projects/{project_id}/confirm", expected=403)
        _expect_atomic_rejection(
            check, conn, supplier_client, project_id,
            "supplier rejection with a legacy project:confirm grant",
            "POST", f"/api/v1/projects/{project_id}/reject",
            {"reason": "供应商不得验收"}, expected=403)
    finally:
        with conn.cursor() as cursor:
            cursor.execute(
                "DELETE FROM role_permissions WHERE role_id=%s AND permission_id=%s",
                (supplier_role["id"], confirm_permission_id),
            )
    rejected = internal_client.call(
        "POST", f"/api/v1/projects/{project_id}/reject", {"reason": "公司验收驳回"})
    submitted_for_withdraw = supplier_client.call(
        "POST", f"/api/v1/projects/{project_id}/submit", {"confirmSide": "COMPANY"})
    _expect_atomic_rejection(
        check, conn, supplier_client, project_id, "supplier withdrawal without explicit permission",
        "POST", f"/api/v1/projects/{project_id}/withdraw", expected=403)
    client.call("PUT", f"/api/v1/admin/roles/{supplier_role['id']}/permissions", {
        "permissionIds": original_supplier_permission_ids + [withdraw_permission_id],
    })
    try:
        withdrawn = supplier_client.call("POST", f"/api/v1/projects/{project_id}/withdraw")
    finally:
        client.call("PUT", f"/api/v1/admin/roles/{supplier_role['id']}/permissions", {
            "permissionIds": original_supplier_permission_ids,
        })
    submitted_for_confirm = internal_client.call(
        "POST", f"/api/v1/projects/{project_id}/submit", {})
    completed = internal_client.call("POST", f"/api/v1/projects/{project_id}/confirm")

    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT action,operator_id,confirm_side,reason,from_status,to_status "
            "FROM project_status_logs WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        history = list(cursor.fetchall())
    workflow_tail = history[-6:]
    project_audits = _audit_actions(conn, project_id)
    _check_equal(
        check,
        "workflow responses persist company-only acceptance states",
        (
            (submitted_for_reject["status"], submitted_for_reject["confirmSide"]),
            (rejected["status"], rejected["confirmSide"]),
            (submitted_for_withdraw["status"], submitted_for_withdraw["confirmSide"]),
            (withdrawn["status"], withdrawn["confirmSide"]),
            (submitted_for_confirm["status"], submitted_for_confirm["confirmSide"]),
            (completed["status"], completed["confirmSide"]),
        ),
        (
            ("PENDING_CONFIRMATION", "COMPANY"),
            ("IN_PROGRESS", None),
            ("PENDING_CONFIRMATION", "COMPANY"),
            ("IN_PROGRESS", None),
            ("PENDING_CONFIRMATION", "COMPANY"),
            ("COMPLETED", None),
        ),
    )
    _check_equal(
        check,
        "workflow status history records the actual supplier and internal actors",
        workflow_tail,
        [
            ("SUBMIT", supplier_user["id"], "COMPANY", None,
             "IN_PROGRESS", "PENDING_CONFIRMATION"),
            ("REJECT", internal_user["id"], "COMPANY", "公司验收驳回",
             "PENDING_CONFIRMATION", "IN_PROGRESS"),
            ("SUBMIT", supplier_user["id"], "COMPANY", None,
             "IN_PROGRESS", "PENDING_CONFIRMATION"),
            ("WITHDRAW", supplier_user["id"], "COMPANY", None,
             "PENDING_CONFIRMATION", "IN_PROGRESS"),
            ("SUBMIT", internal_user["id"], "COMPANY", None,
             "IN_PROGRESS", "PENDING_CONFIRMATION"),
            ("CONFIRM", internal_user["id"], "COMPANY", None,
             "PENDING_CONFIRMATION", "COMPLETED"),
        ],
    )
    _check_equal(
        check,
        "workflow audit actions persist once per accepted transition",
        {
            action: project_audits.count(action)
            for action in ("PROJECT_SUBMIT", "PROJECT_REJECT", "PROJECT_WITHDRAW", "PROJECT_CONFIRM")
        },
        {
            "PROJECT_SUBMIT": 3,
            "PROJECT_REJECT": 1,
            "PROJECT_WITHDRAW": 1,
            "PROJECT_CONFIRM": 1,
        },
    )
    _check_equal(
        check,
        "workflow notifications target only eligible recipients and latest submitters",
        {
            "message_internal": _event_count(
                conn, project_id, "MESSAGE_CREATED", internal_user["id"]),
            "message_confirm_only": _event_count(
                conn, project_id, "MESSAGE_CREATED", confirm_only_user["id"]),
            "submitted_internal": _event_count(
                conn, project_id, "PROJECT_SUBMITTED", internal_user["id"]),
            "submitted_confirm_only": _event_count(
                conn, project_id, "PROJECT_SUBMITTED", confirm_only_user["id"]),
            "rejected_supplier": _event_count(
                conn, project_id, "PROJECT_REJECTED", supplier_user["id"]),
            "withdrawn_internal": _event_count(
                conn, project_id, "PROJECT_WITHDRAWN", internal_user["id"]),
            "withdrawn_confirm_only": _event_count(
                conn, project_id, "PROJECT_WITHDRAWN", confirm_only_user["id"]),
            "withdrawn_supplier": _event_count(
                conn, project_id, "PROJECT_WITHDRAWN", supplier_user["id"]),
            "confirmed_supplier": _event_count(
                conn, project_id, "PROJECT_CONFIRMED", supplier_user["id"]),
        },
        {
            "message_internal": 1,
            "message_confirm_only": 0,
            "submitted_internal": 2,
            "submitted_confirm_only": 0,
            "rejected_supplier": 1,
            "withdrawn_internal": 1,
            "withdrawn_confirm_only": 0,
            "withdrawn_supplier": 1,
            "confirmed_supplier": 0,
        },
    )

    # COMPLETED is read-only for business content. Existing project, file, and
    # message reads remain available, while every mutation below must roll back
    # without status history, audit, activity, or outbox side effects.
    detail = supplier_client.call("GET", f"/api/v1/projects/{project_id}")
    messages = supplier_client.call("GET", f"/api/v1/projects/{project_id}/messages?pageSize=100")
    files = supplier_client.call("GET", f"/api/v1/projects/{project_id}/files?pageSize=100")
    check(
        "completed project content remains readable",
        detail["status"] == "COMPLETED"
        and any(item["id"] == message["id"] for item in messages["list"])
        and any(item["id"] == file_id for item in files["list"])
        and _download_matches(supplier_client, file_id, payload),
    )

    _expect_atomic_rejection(
        check, conn, supplier_client, project_id, "completed upload init",
        "POST", "/api/v1/uploads/init", {
            "projectId": project_id,
            "fileName": "after-completed.pdf",
            "fileSize": 1,
            "fileMd5": "0" * 32,
        })
    _expect_atomic_rejection(
        check, conn, supplier_client, project_id, "completed message create",
        "POST", f"/api/v1/projects/{project_id}/messages", {"content": "不可新增"})
    _expect_atomic_rejection(
        check, conn, client, project_id, "completed message delete",
        "DELETE", f"/api/v1/messages/{message['id']}")
    _expect_atomic_rejection(
        check, conn, client, project_id, "completed file delete",
        "DELETE", f"/api/v1/files/{file_id}")
    _expect_atomic_rejection(
        check, conn, client, project_id, "completed project edit",
        "PUT", f"/api/v1/projects/{project_id}", {
            "name": created["name"],
            "description": "不可编辑",
            "supplierId": supplier["id"],
        })
    _expect_atomic_rejection(
        check, conn, client, project_id, "completed project members",
        "PUT", f"/api/v1/projects/{project_id}/members", {
            "userIds": [internal_user["id"]],
        })
    _expect_atomic_rejection(
        check, conn, supplier_client, project_id, "completed project resubmit",
        "POST", f"/api/v1/projects/{project_id}/submit", {"confirmSide": "COMPANY"})
    _expect_atomic_rejection(
        check, conn, client, project_id, "completed project delete",
        # ProjectWorkflowRules rejects deletion as an invalid request (400).
        "DELETE", f"/api/v1/projects/{project_id}", expected=400)
