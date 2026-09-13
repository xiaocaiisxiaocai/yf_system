"""Focused collaboration notification and dashboard regressions.

The caller owns the disposable database, storage, API process and authenticated
administrator. Existing project activity is deliberately initially unread after
the additive collaboration-read migration; no date or activity-id cutoff exists.
"""

import secrets

from test_business_acceptance import _upload_chunks


def _password():
    return "Yf9!" + secrets.token_urlsafe(9)


def _role(admin, codes):
    permissions = admin.call("GET", "/api/v1/permissions")
    by_code = {item["code"]: item["id"] for item in permissions}
    role = admin.call("POST", "/api/v1/admin/roles", {
        "name": "协作通知回归-" + secrets.token_hex(5),
        "description": "owned isolated collaboration fixture",
    })
    admin.call("PUT", f"/api/v1/admin/roles/{role['id']}/permissions", {
        "permissionIds": [by_code[code] for code in codes],
    })
    return role["id"]


def _internal(admin, Client, conn, role_id, label):
    employee = "collab_" + secrets.token_hex(5)
    password = _password()
    user = admin.call("POST", "/api/v1/admin/users", {
        "employeeNo": employee,
        "password": password,
        "realName": label,
        "email": employee + "@example.invalid",
        "departmentId": None,
        "roleId": role_id,
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (user["id"],))
    actor = Client(admin.base)
    actor.login(employee, password)
    return user, actor


def _supplier(admin, Client, conn, label):
    supplier = admin.call("POST", "/api/v1/admin/suppliers", {
        "name": label + "-" + secrets.token_hex(5),
        "remark": "owned isolated collaboration fixture",
    })
    employee = "collab_supplier_" + secrets.token_hex(4)
    password = _password()
    user = admin.call("POST", f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
        "employeeNo": employee,
        "password": password,
        "realName": label,
        "email": employee + "@example.invalid",
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (user["id"],))
    actor = Client(admin.base)
    actor.login(employee, password)
    return supplier, user, actor


def _project(admin, supplier_id, members):
    project = admin.call("POST", "/api/v1/projects", {
        "name": "协作通知项目-" + secrets.token_hex(5),
        "description": "owned isolated collaboration fixture",
        "supplierId": supplier_id,
    })
    project_id = project["id"]
    admin.call("PUT", f"/api/v1/projects/{project_id}/status", {"status": "IN_PROGRESS"})
    admin.call("PUT", f"/api/v1/projects/{project_id}/members", {"userIds": members})
    return project_id


def _activity_id(conn, activity_type, target_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT id FROM project_activities WHERE activity_type=%s AND target_id=%s ORDER BY id DESC LIMIT 1",
            (activity_type, target_id),
        )
        row = cursor.fetchone()
    if row is None:
        raise AssertionError(f"missing {activity_type} activity for target {target_id}")
    return row[0]


def _read_count(conn, activity_id, user_id):
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT COUNT(*) FROM collaboration_reads WHERE activity_id=%s AND user_id=%s",
            (activity_id, user_id),
        )
        return cursor.fetchone()[0]


def run_collaboration_checks(admin, Client, conn, check):
    role_id = _role(admin, [
        "project:list", "dashboard", "message:create", "file:upload", "project:submit",
    ])
    first_user, first = _internal(admin, Client, conn, role_id, "协作甲")
    second_user, second = _internal(admin, Client, conn, role_id, "协作乙")
    first_supplier, first_supplier_user, first_supplier_client = _supplier(
        admin, Client, conn, "协作供应商甲")
    second_supplier, _, second_supplier_client = _supplier(
        admin, Client, conn, "协作供应商乙")
    project_id = _project(admin, first_supplier["id"], [first_user["id"], second_user["id"]])

    before_own = first.call("GET", "/api/v1/collaboration/summary")
    messages = []
    for index in range(4):
        messages.append(first.call(
            "POST", f"/api/v1/projects/{project_id}/messages",
            {"content": f"协作敏感正文-{index}-{secrets.token_hex(4)}"},
        ))
    after_own = first.call("GET", "/api/v1/collaboration/summary")
    check("own activity changes collaboration revision without creating own unread",
          after_own["latestId"] > before_own["latestId"]
          and after_own["revision"] != before_own["revision"]
          and after_own["unreadCount"] == before_own["unreadCount"])

    page = second.call(
        "GET", "/api/v1/collaboration/notifications?page=1&pageSize=2&unreadOnly=true")
    message_activity_ids = {
        item["targetId"]: item["id"]
        for item in second.call(
            "GET", "/api/v1/collaboration/notifications?page=1&pageSize=100&unreadOnly=false")["list"]
        if item["type"] == "MESSAGE"
    }
    check("notifications are server paged unread project activities with precise targets",
          page["page"] == 1 and page["pageSize"] == 2 and len(page["list"]) == 2
          and page["total"] == page["unreadCount"] >= 5
          and all(message["id"] in message_activity_ids for message in messages))

    hidden_project = _project(admin, second_supplier["id"], [])
    hidden_message = admin.call(
        "POST", f"/api/v1/projects/{hidden_project}/messages",
        {"content": "另一供应商不可见正文"},
    )
    hidden_activity = _activity_id(conn, "MESSAGE", hidden_message["id"])
    visible_activity = message_activity_ids[messages[0]["id"]]
    second.call("POST", "/api/v1/collaboration/reads", {
        "ids": [visible_activity, hidden_activity],
    }, expected=403)
    check("collaboration reads validate the complete scoped id set atomically",
          _read_count(conn, visible_activity, second_user["id"]) == 0
          and _read_count(conn, hidden_activity, second_user["id"]) == 0)

    second.call("POST", "/api/v1/collaboration/reads", {"ids": [visible_activity]})
    stable = second.call("GET", "/api/v1/collaboration/summary")
    second.call("POST", "/api/v1/collaboration/reads", {"ids": [visible_activity, visible_activity]})
    repeated = second.call("GET", "/api/v1/collaboration/summary")
    supplier_summary = first_supplier_client.call("GET", "/api/v1/collaboration/summary")
    check("notification receipts are per-user and idempotent",
          stable == repeated and stable["unreadCount"] + 1 == page["unreadCount"]
          and supplier_summary["unreadCount"] >= page["unreadCount"])
    second.call("POST", "/api/v1/collaboration/reads", {"ids": list(range(1, 102))}, expected=400)

    second.call("POST", "/api/v1/messages/read", {"ids": [messages[1]["id"]]})
    dashboard_unread = second.call(
        "GET", "/api/v1/dashboard/messages?page=1&pageSize=2&unreadOnly=true")
    check("dashboard unread filtering and pagination are fully server side",
          dashboard_unread["pageSize"] == 2 and len(dashboard_unread["list"]) == 2
          and dashboard_unread["total"] == 3
          and all(item["unread"] and item["id"] != messages[1]["id"] for item in dashboard_unread["list"]))

    removed_content = messages[2]["content"]
    removed_activity = message_activity_ids[messages[2]["id"]]
    admin.call("DELETE", f"/api/v1/messages/{messages[2]['id']}")
    removed_page = second.call(
        "GET", "/api/v1/collaboration/notifications?page=1&pageSize=100&unreadOnly=false")
    removed = next(item for item in removed_page["list"] if item["id"] == removed_activity)
    check("removed message notification does not leak body and disables its target",
          removed["summary"] is None and not removed["targetAvailable"]
          and removed_content not in str(removed_page))

    first_supplier_page = first_supplier_client.call(
        "GET", "/api/v1/collaboration/notifications?page=1&pageSize=100&unreadOnly=false")
    check("supplier notification scope excludes another supplier tenant",
          all(item["projectId"] == project_id for item in first_supplier_page["list"]))
    second_supplier_page = second_supplier_client.call(
        "GET", "/api/v1/collaboration/notifications?page=1&pageSize=100&unreadOnly=false")
    check("supplier can see only its own tenant activity",
          second_supplier_page["list"] and all(
              item["projectId"] == hidden_project for item in second_supplier_page["list"]))

    dashboard_only_role = _role(admin, ["dashboard"])
    dashboard_user, dashboard_client = _internal(
        admin, Client, conn, dashboard_only_role, "仅工作台协作成员")
    admin.call("PUT", f"/api/v1/projects/{project_id}/members", {
        "userIds": [first_user["id"], second_user["id"], dashboard_user["id"]],
    })
    dashboard_client.call("GET", "/api/v1/collaboration/summary", expected=403)
    dashboard_messages = dashboard_client.call(
        "GET", "/api/v1/dashboard/messages?page=1&pageSize=10&unreadOnly=true")
    check("collaboration requires project menu while dashboard messages retain dashboard visibility",
          dashboard_messages["total"] == 3
          and all(item["projectId"] == project_id for item in dashboard_messages["list"]))

    file_id, _, _ = _upload_chunks(first, project_id, "协作链接.pdf", b"collaboration-link")
    first.call("POST", f"/api/v1/projects/{project_id}/submit", {"confirmSide": "SUPPLIER"})
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT event_type,body FROM email_outbox WHERE project_id=%s ORDER BY id",
            (project_id,),
        )
        outbox = cursor.fetchall()
    check("message file and workflow emails contain precise project targets",
          any(event == "MESSAGE_CREATED" and f"/projects/{project_id}?tab=messages&target={messages[0]['id']}" in body
              for event, body in outbox)
          and any(event == "FILE_UPLOADED" and f"/projects/{project_id}?tab=files&target={file_id}" in body
                  for event, body in outbox)
          and any(event == "PROJECT_SUBMITTED" and f"/projects/{project_id}?tab=activity" in body
                  for event, body in outbox))
