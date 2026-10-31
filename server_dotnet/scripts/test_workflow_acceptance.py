"""Persistent project-lifecycle acceptance using caller-owned isolated resources.

The caller supplies an authenticated administrator, disposable database connection,
and API Client type. The mail worker must remain disabled; notification assertions
stop at durable PENDING outbox rows.
"""

import secrets

from upload_contract import init_request

from test_business_acceptance import (
    _activate_user,
    _approve_materials,
    _create_project_group,
    _download_matches,
    _password,
    _upload_chunks,
)


def _project_snapshot(conn, project_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT status,name,description,material_review_status,"
            "material_review_file_id,material_review_reason FROM projects WHERE id=%s",
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
    return project, history, outbox, files, messages, uploads, audit, activities


def _expect_atomic_rejection(check, conn, actor, project_id, label, method, path,
                             body=None, expected=409):
    before = _project_snapshot(conn, project_id)
    response = actor.call(method, path, body, expected=expected)
    after = _project_snapshot(conn, project_id)
    check(label + " is rejected without state audit or outbox writes", after == before)
    return response


def _event_count(conn, project_id, event_type, recipient_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT COUNT(*) FROM email_outbox WHERE project_id=%s AND event_type=%s "
            "AND recipient_user_id=%s AND status='PENDING' AND retry_count=0",
            (project_id, event_type, recipient_id),
        )
        return cursor.fetchone()[0]


def _event_total(conn, project_id, event_type):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT COUNT(*) FROM email_outbox WHERE project_id=%s AND event_type=%s",
            (project_id, event_type),
        )
        return cursor.fetchone()[0]


def _check_equal(check, label, actual, expected):
    if actual != expected:
        raise AssertionError(f"{label}: expected {expected!r}, got {actual!r}")
    check(label, True)


def _create_owner_section(client, suffix):
    division = client.call("POST", "/api/v1/admin/departments", {
        "name": "工作流验收事业部-" + suffix,
        "parentId": None,
        "sortNo": 100,
    })
    department = client.call("POST", "/api/v1/admin/departments", {
        "name": "工作流验收部门-" + suffix,
        "parentId": division["id"],
        "sortNo": 101,
    })
    section = client.call("POST", "/api/v1/admin/departments", {
        "name": "工作流验收课别-" + suffix,
        "parentId": department["id"],
        "sortNo": 102,
    })
    return section["id"]


def _status_history(conn, project_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT action,operator_id,reason,from_status,to_status "
            "FROM project_status_logs WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        return list(cursor.fetchall())


def _audit_actions(conn, project_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT action FROM audit_logs WHERE target_type='project' AND target_id=%s ORDER BY id",
            (str(project_id),),
        )
        return [row[0] for row in cursor.fetchall()]


def run_workflow_acceptance(client, Client, conn, check):
    suffix = secrets.token_hex(5)
    owner_section_id = _create_owner_section(client, suffix)
    permissions = client.call("GET", "/api/v1/permissions")
    permission_ids = {permission["code"]: permission["id"] for permission in permissions}
    supplier_role = next(
        role for role in client.call("GET", "/api/v1/admin/roles?pageSize=100")["list"]
        if role["name"] == "供应商人员")
    check(
        "the acceptance permissions are retired and the default supplier role can return material",
        not {"project:submit", "project:confirm", "project:withdraw"} & set(permission_ids)
        and permission_ids["file:upload"] in supplier_role["permissionIds"],
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
        if role["name"] == "项目管理员")
    internal_employee = "wf_internal_" + suffix
    internal_initial = _password()
    internal_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": internal_employee,
        "password": internal_initial,
        "realName": "工作流验收项目负责人",
        "email": internal_employee + "@example.invalid",
        "departmentId": owner_section_id,
        "roleId": internal_role["id"],
    })
    internal_client = _activate_user(
        client, Client, check, internal_employee, internal_initial,
        internal_user["id"], "workflow project owner")

    menuless_role = client.call("POST", "/api/v1/admin/roles", {
        "name": "全局查看无项目菜单-" + suffix,
        "description": "verifies notification recipients can actually open the project",
    })
    client.call("PUT", f"/api/v1/admin/roles/{menuless_role['id']}/permissions", {
        "permissionIds": [permission_ids["dashboard"], permission_ids["project:view_all"]],
    })
    menuless_employee = "wf_menuless_" + suffix
    menuless_initial = _password()
    menuless_user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": menuless_employee,
        "password": menuless_initial,
        "realName": "全局查看无项目菜单用户",
        "email": menuless_employee + "@example.invalid",
        "departmentId": owner_section_id,
        "roleId": menuless_role["id"],
    })
    menuless_client = _activate_user(
        client, Client, check, menuless_employee, menuless_initial,
        menuless_user["id"], "workflow menu-less member")
    menuless_client.call("GET", "/api/v1/project-groups", expected=403)

    group, created = _create_project_group(
        internal_client, client, conn, supplier["id"],
        "工作流完整验收项目-" + suffix)
    project_id = created["id"]
    group_id = group["id"]
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT action,from_status,to_status FROM project_group_status_logs "
            "WHERE project_group_id=%s ORDER BY id",
            (group_id,),
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
        and "confirmSide" not in created
        and "latestSubmissionId" not in created
        and draft_history == [("CREATE", None, "DRAFT")]
        and _audit_actions(conn, project_id) == ["PROJECT_CREATE"]
        and draft_outbox == 0,
    )

    started = client.call("PUT", f"/api/v1/projects/{project_id}/status", {
        "status": "IN_PROGRESS",
    })
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
        # Each management transition mails the supplier side only (one switch per event).
        cursor.execute(
            "SELECT o.event_type,u.user_type FROM email_outbox o "
            "LEFT JOIN users u ON u.id=o.recipient_user_id WHERE o.project_id=%s",
            (project_id,),
        )
        management_outbox = list(cursor.fetchall())
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
        and {event for event, _ in management_outbox}
        == {"PROJECT_STARTED", "PROJECT_TERMINATED", "PROJECT_RESTARTED"}
        and all(user_type == "SUPPLIER" for _, user_type in management_outbox),
    )

    payload = (b"workflow-completed-read-boundary\n" * 12000) + b"EOF"
    file_id, _, _ = _upload_chunks(
        supplier_client, project_id, "workflow-boundary.pdf", payload)
    check(
        "file upload notifies the current owner but not an internal user without project:list",
        _event_count(conn, project_id, "FILE_UPLOADED", internal_user["id"]) == 1
        and _event_count(conn, project_id, "FILE_UPLOADED", menuless_user["id"]) == 0,
    )
    message = supplier_client.call(
        "POST", f"/api/v1/projects/{project_id}/messages",
        {"content": "完成前创建，完成后仍应可读但不可删除"})

    for retired in ("submit", "withdraw", "confirm", "reject"):
        _expect_atomic_rejection(
            check, conn, supplier_client if retired in ("submit", "withdraw") else internal_client,
            project_id, f"retired {retired} endpoint",
            "POST", f"/api/v1/projects/{project_id}/{retired}",
            {"confirmSide": "COMPANY", "expectedSubmissionId": 1, "reason": "已退役"}, expected=404)

    material_detail = internal_client.call("GET", f"/api/v1/projects/{project_id}")
    _check_equal(
        check,
        "supplier upload leaves the material pending the responsible user's review",
        (material_detail["materialReviewStatus"], material_detail["latestSupplierFileId"]),
        ("PENDING", file_id),
    )
    owner_pending = internal_client.call("GET", "/api/v1/dashboard/pending-projects?page=1&pageSize=100")
    owner_summary = internal_client.call("GET", "/api/v1/dashboard/summary")
    supplier_pending = supplier_client.call("GET", "/api/v1/dashboard/pending-projects?page=1&pageSize=100")
    pending_group = supplier_client.call("GET", f"/api/v1/project-groups/{group_id}")
    pending_list = internal_client.call("GET", "/api/v1/project-groups?page=1&pageSize=100")
    pending_group_item = next(item for item in pending_list["list"] if item["id"] == group_id)
    check(
        "pending material is the owner's dashboard task and the main project's pending count",
        any(item["id"] == project_id and item["materialReviewStatus"] == "PENDING"
            for item in owner_pending["list"])
        and owner_summary["pendingTasks"] >= 1
        and all(item["id"] != project_id for item in supplier_pending["list"])
        and pending_group["group"]["materialPendingCount"] == 1
        and pending_group_item["materialPendingCount"] == 1,
    )
    menuless_client.call("GET", "/api/v1/dashboard/pending-projects?page=1&pageSize=100", expected=403)
    check("dashboard tasks require project:list", True)
    summary = supplier_client.call("GET", f"/api/v1/projects/{project_id}/summary")
    _check_equal(check, "project summary exposes only unread and activity revision",
                 sorted(summary), ["activityRevision", "unreadMessages"])

    material_path = f"/api/v1/projects/{project_id}/materials"
    _expect_atomic_rejection(
        check, conn, supplier_client, project_id, "supplier material approval",
        "POST", material_path + "/approve", {"expectedSupplierFileId": file_id}, expected=403)
    _expect_atomic_rejection(
        check, conn, client, project_id, "non-responsible internal material approval",
        "POST", material_path + "/approve", {"expectedSupplierFileId": file_id}, expected=403)
    stale_material = _expect_atomic_rejection(
        check, conn, internal_client, project_id, "material approval against an older supplier file",
        "POST", material_path + "/approve", {"expectedSupplierFileId": file_id - 1})
    _expect_atomic_rejection(
        check, conn, internal_client, project_id, "material return without a reason",
        "POST", material_path + "/return", {"expectedSupplierFileId": file_id}, expected=400)
    returned_material = internal_client.call("POST", material_path + "/return", {
        "expectedSupplierFileId": file_id,
        "reason": "缺少动作流程表",
    })
    returned_again = _expect_atomic_rejection(
        check, conn, internal_client, project_id, "returning already returned material",
        "POST", material_path + "/return", {"expectedSupplierFileId": file_id, "reason": "再次退回"})
    supplier_returned_tasks = supplier_client.call(
        "GET", "/api/v1/dashboard/pending-projects?page=1&pageSize=100")
    check(
        "responsible user returns the material with a supplier task and notification",
        stale_material["code"] == 40903
        and (returned_material["materialReviewStatus"], returned_material["materialReviewReason"])
        == ("RETURNED", "缺少动作流程表")
        and returned_again["code"] == 40901
        and any(item["id"] == project_id and item["materialReviewStatus"] == "RETURNED"
                for item in supplier_returned_tasks["list"])
        and _event_count(conn, project_id, "PROJECT_MATERIAL_RETURNED", supplier_user["id"]) == 1,
    )

    second_file, _, _ = _upload_chunks(
        supplier_client, project_id, "workflow-second.pdf", b"workflow-second-material")
    _check_equal(
        check,
        "a new supplier file sends returned material back to review",
        internal_client.call("GET", f"/api/v1/projects/{project_id}")["materialReviewStatus"],
        "PENDING",
    )
    completed_once = internal_client.call("POST", material_path + "/approve", {
        "expectedSupplierFileId": second_file,
    })
    group_after_approval = internal_client.call("GET", f"/api/v1/project-groups/{group_id}")["group"]

    reopen_path = f"/api/v1/projects/{project_id}/reopen"
    reopen_body = {"expectedUpdatedAt": completed_once["updatedAt"], "reason": "动作节拍需要返工"}
    _expect_atomic_rejection(
        check, conn, supplier_client, project_id, "supplier reopen", "POST", reopen_path, reopen_body,
        expected=403)
    _expect_atomic_rejection(
        check, conn, client, project_id, "non-responsible internal reopen", "POST", reopen_path, reopen_body,
        expected=403)
    _expect_atomic_rejection(
        check, conn, internal_client, project_id, "reopen without a reason", "POST", reopen_path,
        {"expectedUpdatedAt": completed_once["updatedAt"]}, expected=400)
    stale_reopen = _expect_atomic_rejection(
        check, conn, internal_client, project_id, "reopen against an older version", "POST", reopen_path,
        {"expectedUpdatedAt": completed_once["createdAt"], "reason": "陈旧版本"})
    reopened = internal_client.call("POST", reopen_path, reopen_body)
    group_after_reopen = internal_client.call("GET", f"/api/v1/project-groups/{group_id}")["group"]
    completed = internal_client.call("POST", material_path + "/approve", {
        "expectedSupplierFileId": second_file,
    })
    group_after_completion = internal_client.call("GET", f"/api/v1/project-groups/{group_id}")["group"]
    check(
        "approval completes, reopening returns the material, and approval completes again",
        completed_once["status"] == "COMPLETED"
        and completed_once["materialReviewStatus"] is None
        and group_after_approval["status"] == "COMPLETED"
        and stale_reopen["code"] == 40903
        and (reopened["status"], reopened["materialReviewStatus"], reopened["materialReviewReason"])
        == ("IN_PROGRESS", "RETURNED", "动作节拍需要返工")
        and group_after_reopen["status"] == "IN_PROGRESS"
        and completed["status"] == "COMPLETED"
        and group_after_completion["status"] == "COMPLETED",
    )
    _check_equal(
        check,
        "workflow status history records the responsible user's approvals and reopen",
        _status_history(conn, project_id)[-3:],
        [
            ("MATERIAL_APPROVE", internal_user["id"], None, "IN_PROGRESS", "COMPLETED"),
            ("REOPEN", internal_user["id"], "动作节拍需要返工", "COMPLETED", "IN_PROGRESS"),
            ("MATERIAL_APPROVE", internal_user["id"], None, "IN_PROGRESS", "COMPLETED"),
        ],
    )
    project_audits = _audit_actions(conn, project_id)
    _check_equal(
        check,
        "workflow audit actions persist once per accepted transition",
        {
            action: project_audits.count(action)
            for action in ("PROJECT_MATERIAL_RETURN", "PROJECT_MATERIAL_APPROVE", "PROJECT_REOPEN",
                           "PROJECT_SUBMIT", "PROJECT_CONFIRM", "PROJECT_REJECT", "PROJECT_WITHDRAW")
        },
        {
            "PROJECT_MATERIAL_RETURN": 1,
            "PROJECT_MATERIAL_APPROVE": 2,
            "PROJECT_REOPEN": 1,
            "PROJECT_SUBMIT": 0,
            "PROJECT_CONFIRM": 0,
            "PROJECT_REJECT": 0,
            "PROJECT_WITHDRAW": 0,
        },
    )
    _check_equal(
        check,
        "workflow notifications target only eligible recipients",
        {
            "message_internal": _event_count(
                conn, project_id, "MESSAGE_CREATED", internal_user["id"]),
            "message_menuless": _event_count(
                conn, project_id, "MESSAGE_CREATED", menuless_user["id"]),
            "returned_supplier": _event_count(
                conn, project_id, "PROJECT_MATERIAL_RETURNED", supplier_user["id"]),
            "approved_supplier": _event_count(
                conn, project_id, "PROJECT_MATERIAL_APPROVED", supplier_user["id"]),
            "approved_internal": _event_count(
                conn, project_id, "PROJECT_MATERIAL_APPROVED", internal_user["id"]),
            "retired_events": sum(
                _event_total(conn, project_id, event)
                for event in ("PROJECT_SUBMITTED", "PROJECT_CONFIRMED", "PROJECT_REJECTED", "PROJECT_WITHDRAWN")),
        },
        {
            "message_internal": 1,
            "message_menuless": 0,
            "returned_supplier": 1,
            "approved_supplier": 2,
            "approved_internal": 0,
            "retired_events": 0,
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
        "POST", "/api/v1/uploads/init",
        init_request(project_id, "after-completed.pdf", b"\0"))
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
        check, conn, internal_client, project_id, "completed material return",
        "POST", f"/api/v1/projects/{project_id}/materials/return",
        {"expectedSupplierFileId": second_file, "reason": "已完成"})
    _expect_atomic_rejection(
        check, conn, client, project_id, "completed project delete",
        # ProjectWorkflowRules rejects deletion as an invalid request (400).
        "DELETE", f"/api/v1/projects/{project_id}", expected=400)
