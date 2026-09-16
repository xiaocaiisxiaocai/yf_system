"""Focused project workflow regressions using caller-owned isolated resources.

The caller supplies the already authenticated administrator client, disposable
database connection, and API Client type. The mail worker must remain disabled.
"""

import secrets
import threading
import time

from test_business_acceptance import _create_started_project, _project_metadata


def _password():
    return "Yf9!" + secrets.token_urlsafe(9)


def _new_role(client, codes):
    permissions = client.call("GET", "/api/v1/permissions")
    by_code = {item["code"]: item["id"] for item in permissions}
    role = client.call("POST", "/api/v1/admin/roles", {
        "name": "项目并发回归-" + secrets.token_hex(5),
        "description": "owned isolated project remediation fixture",
    })
    client.call("PUT", f"/api/v1/admin/roles/{role['id']}/permissions", {
        "permissionIds": [by_code[code] for code in codes],
    })
    return role["id"]


def _new_internal(client, Client, conn, role_id, label, section_id):
    employee = "project_" + secrets.token_hex(5)
    password = _password()
    user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": employee,
        "password": password,
        "realName": label,
        "email": employee + "@example.invalid",
        "departmentId": section_id,
        "roleId": role_id,
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (user["id"],))
    actor = Client(client.base)
    actor.login(employee, password)
    return user, actor


def _new_supplier(client, Client, conn):
    supplier = client.call("POST", "/api/v1/admin/suppliers", {
        "name": "项目并发供应商-" + secrets.token_hex(5),
        "remark": "owned isolated project remediation fixture",
    })
    employee = "project_supplier_" + secrets.token_hex(4)
    password = _password()
    user = client.call("POST", f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
        "employeeNo": employee,
        "password": password,
        "realName": "项目并发供应商",
        "email": employee + "@example.invalid",
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (user["id"],))
    actor = Client(client.base)
    actor.login(employee, password)
    return supplier, user, actor


def _new_supplier_account(client, Client, conn, supplier_id, label):
    employee = "project_supplier_" + secrets.token_hex(4)
    password = _password()
    user = client.call("POST", f"/api/v1/admin/suppliers/{supplier_id}/accounts", {
        "employeeNo": employee,
        "password": password,
        "realName": label,
        "email": employee + "@example.invalid",
    })
    with conn.cursor() as cursor:
        cursor.execute("UPDATE users SET must_change_password=0 WHERE id=%s", (user["id"],))
    actor = Client(client.base)
    actor.login(employee, password)
    return user, actor


def _section(client, suffix):
    division = client.call("POST", "/api/v1/admin/departments", {
        "name": "项目并发事业部-" + suffix,
        "parentId": None,
        "sortNo": 120,
    })
    department = client.call("POST", "/api/v1/admin/departments", {
        "name": "项目并发部门-" + suffix,
        "parentId": division["id"],
        "sortNo": 121,
    })
    section = client.call("POST", "/api/v1/admin/departments", {
        "name": "项目并发课别-" + suffix,
        "parentId": department["id"],
        "sortNo": 122,
    })
    return section["id"]


def _new_project(client, conn, supplier_id, responsible_user_id, name):
    return _create_started_project(
        client, conn, supplier_id, responsible_user_id,
        name + "-" + secrets.token_hex(5))


def _insert_file(conn, project_id, uploader_id):
    token = secrets.token_hex(8)
    with conn.cursor() as cursor:
        cursor.execute(
            "INSERT INTO files(project_id,uploader_id,direction,original_name,stored_name,ext,size_bytes,"
            "mime_type,sha256,storage_path,status,deleted_at,created_at) "
            "VALUES(%s,%s,'C2S',%s,%s,'pdf',1,'application/pdf',%s,%s,'AVAILABLE',NULL,UTC_TIMESTAMP(3))",
            (project_id, uploader_id, token + ".pdf", token + ".pdf", "0" * 64, token + ".pdf"),
        )
        return cursor.lastrowid


def _request_in_thread(client, method, path, body, expected):
    result = {}
    started = threading.Event()

    def invoke():
        started.set()
        try:
            result["value"] = client.call(method, path, body, expected=expected)
        except BaseException as error:  # returned to the main test thread
            result["error"] = error

    worker = threading.Thread(target=invoke, daemon=True)
    worker.start()
    if not started.wait(2):
        raise AssertionError("concurrent request did not start")
    return worker, result


def _wait_for_project_lock(conn, worker, project_id):
    deadline = time.monotonic() + 5
    last_waits = []
    blocked_query_since = None
    blocked_process_id = None
    with conn.cursor() as cursor:
        cursor.execute("SELECT DATABASE()")
        expected_table = f"`{cursor.fetchone()[0]}`.`projects`"
        cursor.execute(
            "SELECT trx_rows_locked FROM information_schema.innodb_trx "
            "WHERE trx_mysql_thread_id=CONNECTION_ID()"
        )
        holder = cursor.fetchone()
    if holder is None or holder[0] < 1:
        raise AssertionError("fixture transaction does not hold an InnoDB row lock")
    expected_query = f"select id from projects where id={project_id} for update"
    while time.monotonic() < deadline:
        if not worker.is_alive():
            raise AssertionError("request completed before reaching the held project lock")
        with conn.cursor() as cursor:
            cursor.execute(
                "SELECT requested.lock_table,requested.lock_index,requested.lock_type,requested.lock_mode,"
                "blocking.lock_table,blocking.lock_index,blocking.lock_type,blocking.lock_mode,"
                "LEFT(waiting.trx_query,160) "
                "FROM information_schema.innodb_lock_waits w "
                "JOIN information_schema.innodb_locks requested ON requested.lock_id=w.requested_lock_id "
                "JOIN information_schema.innodb_locks blocking ON blocking.lock_id=w.blocking_lock_id "
                "LEFT JOIN information_schema.innodb_trx waiting ON waiting.trx_id=w.requesting_trx_id "
                "WHERE requested.lock_table=%s",
                (expected_table,),
            )
            last_waits = list(cursor.fetchall())
            if last_waits:
                return
            cursor.execute(
                "SELECT id,state,info FROM information_schema.processlist "
                "WHERE db=DATABASE() AND id<>CONNECTION_ID() AND command='Query'"
            )
            processes = list(cursor.fetchall())
        matching = [row for row in processes
                    if " ".join((row[2] or "").lower().split()) == expected_query]
        if len(matching) == 1:
            if blocked_process_id != matching[0][0]:
                blocked_process_id = matching[0][0]
                blocked_query_since = time.monotonic()
            elif time.monotonic() - blocked_query_since >= 0.25:
                # MySQL 5.7 can keep this exact SELECT ... FOR UPDATE in the
                # optimizer's `statistics` stage without publishing an
                # INNODB_LOCK_WAITS row. Repeated observation of the exact
                # project-id lock statement is the synchronization barrier.
                return
        else:
            blocked_process_id = None
            blocked_query_since = None
        time.sleep(0.02)
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT id,command,time,state,LEFT(info,160) FROM information_schema.processlist "
            "WHERE db=DATABASE() ORDER BY id"
        )
        processes = list(cursor.fetchall())
        cursor.execute(
            "SELECT trx_state,trx_started,trx_requested_lock_id,trx_wait_started,"
            "trx_mysql_thread_id,LEFT(trx_query,160) FROM information_schema.innodb_trx "
            "WHERE trx_mysql_thread_id IN (SELECT id FROM information_schema.processlist WHERE db=DATABASE()) "
            "ORDER BY trx_mysql_thread_id"
        )
        transactions = list(cursor.fetchall())
    raise AssertionError(
        "request did not wait on the held project lock; observed waits=" + repr(last_waits)
        + "; processes=" + repr(processes) + "; transactions=" + repr(transactions))


def _finish(worker, result):
    worker.join(10)
    if worker.is_alive():
        raise AssertionError("concurrent request did not finish")
    if "error" in result:
        raise result["error"]
    return result.get("value")


def _assert_no_submit_side_effect(conn, project_id):
    with conn.cursor() as cursor:
        cursor.execute("SELECT status,confirm_side FROM projects WHERE id=%s", (project_id,))
        state = cursor.fetchone()
        cursor.execute(
            "SELECT COUNT(*) FROM project_status_logs WHERE project_id=%s AND action='SUBMIT'",
            (project_id,),
        )
        submits = cursor.fetchone()[0]
    return state == ("IN_PROGRESS", None) and submits == 0


def run_project_remediation_checks(client, Client, conn, check):
    section_id = _section(client, secrets.token_hex(5))
    supplier, supplier_user, supplier_client = _new_supplier(client, Client, conn)
    dashboardless_role_id = _new_role(client, ["project:list"])
    dashboardless_user, dashboardless_client = _new_internal(
        client, Client, conn, dashboardless_role_id, "无工作台权限用户", section_id)
    dashboardless_client.call("GET", "/api/v1/project-groups")
    dashboardless_client.call("GET", "/api/v1/dashboard/summary", expected=403)
    dashboardless_client.call("GET", "/api/v1/dashboard/pending-projects", expected=403)
    check("dashboard endpoints require the dashboard menu permission", True)

    # Removing the project menu must revoke both the list and resource read paths.
    no_project_role_id = _new_role(client, [])
    no_project_user, no_project_client = _new_internal(
        client, Client, conn, no_project_role_id, "无项目菜单用户", section_id)
    creator_role_id = _new_role(client, ["project:list", "project:create"])
    _, creator_client = _new_internal(
        client, Client, conn, creator_role_id, "项目创建控制用户", section_id)
    create_only_role_id = _new_role(client, ["project:create"])
    _, create_only_client = _new_internal(
        client, Client, conn, create_only_role_id, "缺项目菜单创建用户", section_id)
    unrelated_supplier = client.call("POST", "/api/v1/admin/suppliers", {
        "name": "项目选项无关供应商-" + secrets.token_hex(5),
        "remark": "owned isolated project option fixture",
    })
    access_project = _new_project(client, conn, supplier["id"], dashboardless_user["id"], "项目菜单撤销")
    no_project_client.call("GET", "/api/v1/project-groups", expected=403)
    no_project_client.call("GET", f"/api/v1/projects/{access_project}", expected=403)
    no_project_client.call("GET", f"/api/v1/projects/{access_project}/messages", expected=403)
    no_project_client.call("GET", "/api/v1/supplier-options", expected=403)
    no_project_client.call("GET", "/api/v1/project-owner-options", expected=403)
    create_only_client.call("POST", "/api/v1/project-groups", {
        "name": "不应创建-" + secrets.token_hex(5),
        "description": "project menu gate regression",
        "supplierId": supplier["id"],
        **_project_metadata(conn, dashboardless_user["id"]),
        "subprojectNames": ["不应创建-子项目-" + secrets.token_hex(5)],
    }, expected=403)
    dashboardless_client.call("GET", "/api/v1/project-owner-options", expected=403)
    scoped_supplier_options = dashboardless_client.call("GET", "/api/v1/supplier-options")
    creator_supplier_options = creator_client.call("GET", "/api/v1/supplier-options")
    owner_options = creator_client.call("GET", "/api/v1/project-owner-options")
    check(
        "project options require permissions and only expose the actor's supplier scope",
        {item["id"] for item in scoped_supplier_options} == {supplier["id"]}
        and {supplier["id"], unrelated_supplier["id"]}.issubset(
            {item["id"] for item in creator_supplier_options})
        and any(item["id"] == dashboardless_user["id"] for item in owner_options)
        and all(item["id"] != no_project_user["id"] for item in owner_options),
    )

    permissions = client.call("GET", "/api/v1/permissions")
    project_list_id = next(item["id"] for item in permissions if item["code"] == "project:list")
    role_page = client.call("GET", "/api/v1/admin/roles")
    supplier_role = next(role for role in role_page["list"] if role["name"] == "供应商人员")
    supplier_role_permissions = supplier_role["permissionIds"]
    try:
        client.call("PUT", f"/api/v1/admin/roles/{supplier_role['id']}/permissions", {
            "permissionIds": [item for item in supplier_role_permissions if item != project_list_id],
        })
        supplier_client.call("GET", "/api/v1/project-groups", expected=403)
        supplier_client.call("GET", f"/api/v1/projects/{access_project}", expected=403)
        supplier_client.call("GET", f"/api/v1/projects/{access_project}/messages", expected=403)
    finally:
        client.call("PUT", f"/api/v1/admin/roles/{supplier_role['id']}/permissions", {
            "permissionIds": supplier_role_permissions,
        })
    check("project menu revocation blocks existing internal users and supplier participants", True)

    role_id = _new_role(client, ["project:list", "project:withdraw"])
    view_all_role_id = _new_role(client, ["project:list", "project:view_all", "project:submit"])
    reviewer_role_id = _new_role(
        client, ["dashboard", "project:list", "project:view_all", "project:confirm"])
    old_user, old_client = _new_internal(client, Client, conn, role_id, "旧内部用户", section_id)
    new_user, _ = _new_internal(client, Client, conn, role_id, "新内部用户", section_id)
    view_all_user, view_all_client = _new_internal(
        client, Client, conn, view_all_role_id, "全局查看用户", section_id)
    reviewer_user, reviewer_client = _new_internal(
        client, Client, conn, reviewer_role_id, "全局验收用户", section_id)
    new_supplier_user, new_supplier_client = _new_supplier_account(
        client, Client, conn, supplier["id"], "项目并发供应商新账号")

    # A submit waiting behind the project's row lock must see a just-committed
    # deletion of the final file. Under the former RR snapshot it incorrectly
    # entered PENDING_CONFIRMATION.
    deleted_project = _new_project(client, conn, supplier["id"], old_user["id"], "并发删除文件")
    deleted_file = _insert_file(conn, deleted_project, old_user["id"])
    conn.begin()
    try:
        with conn.cursor() as cursor:
            cursor.execute("SELECT id FROM projects WHERE id=%s FOR UPDATE", (deleted_project,))
            cursor.execute(
                "UPDATE files SET status='DELETED',deleted_at=UTC_TIMESTAMP(3) WHERE id=%s",
                (deleted_file,),
            )
        submit_client = Client(client.base)
        submit_client.token = supplier_client.token
        worker, result = _request_in_thread(
            submit_client, "POST", f"/api/v1/projects/{deleted_project}/submit",
            {}, 409,
        )
        _wait_for_project_lock(conn, worker, deleted_project)
        conn.commit()
    except BaseException:
        conn.rollback()
        raise
    _finish(worker, result)
    check("project submit sees concurrent deletion of final available file",
          _assert_no_submit_side_effect(conn, deleted_project))

    # The same interleaving must observe a newly committed active upload.
    upload_project = _new_project(client, conn, supplier["id"], old_user["id"], "并发活动上传")
    _insert_file(conn, upload_project, old_user["id"])
    upload_id = str(__import__("uuid").uuid4())
    conn.begin()
    try:
        with conn.cursor() as cursor:
            cursor.execute("SELECT id FROM projects WHERE id=%s FOR UPDATE", (upload_project,))
            cursor.execute(
                "INSERT INTO upload_sessions(id,project_id,uploader_id,file_name,file_size,file_md5,chunk_size,"
                "total_chunks,temp_dir,status,result_file_id,expires_at,created_at,updated_at) "
                "VALUES(%s,%s,%s,'active.pdf',1,NULL,262144,1,%s,'UPLOADING',NULL,"
                "DATE_ADD(UTC_TIMESTAMP(3),INTERVAL 1 HOUR),UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))",
                (upload_id, upload_project, old_user["id"], "owned/" + upload_id),
            )
        submit_client = Client(client.base)
        submit_client.token = supplier_client.token
        worker, result = _request_in_thread(
            submit_client, "POST", f"/api/v1/projects/{upload_project}/submit",
            {}, 409,
        )
        _wait_for_project_lock(conn, worker, upload_project)
        conn.commit()
    except BaseException:
        conn.rollback()
        raise
    _finish(worker, result)
    check("project submit sees concurrent creation of active upload",
          _assert_no_submit_side_effect(conn, upload_project))

    # A previous submitter must not withdraw a newer submit that becomes visible
    # only after waiting for the project lock.
    withdraw_project = _new_project(client, conn, supplier["id"], old_user["id"], "并发旧提交者撤回")
    _insert_file(conn, withdraw_project, old_user["id"])
    old_submission = supplier_client.call(
        "POST", f"/api/v1/projects/{withdraw_project}/submit", {})
    reviewer_client.call("POST", f"/api/v1/projects/{withdraw_project}/reject", {
        "reason": "建立旧提交历史",
        "expectedSubmissionId": old_submission["latestSubmissionId"],
    })
    conn.begin()
    try:
        with conn.cursor() as cursor:
            cursor.execute("SELECT id FROM projects WHERE id=%s FOR UPDATE", (withdraw_project,))
            cursor.execute(
                "UPDATE projects SET status='PENDING_CONFIRMATION',confirm_side='COMPANY',"
                "updated_at=UTC_TIMESTAMP(3) WHERE id=%s",
                (withdraw_project,),
            )
            cursor.execute(
                "INSERT INTO project_status_logs(project_id,from_status,to_status,action,operator_id,"
                "confirm_side,reason,created_at) VALUES(%s,'IN_PROGRESS','PENDING_CONFIRMATION','SUBMIT',"
                "%s,'COMPANY',NULL,UTC_TIMESTAMP(3))",
                (withdraw_project, new_supplier_user["id"]),
            )
            current_submission_id = cursor.lastrowid
        worker, result = _request_in_thread(
            supplier_client, "POST", f"/api/v1/projects/{withdraw_project}/withdraw",
            {"expectedSubmissionId": current_submission_id}, 403,
        )
        _wait_for_project_lock(conn, worker, withdraw_project)
        conn.commit()
    except BaseException:
        conn.rollback()
        raise
    _finish(worker, result)
    with conn.cursor() as cursor:
        cursor.execute("SELECT status,confirm_side FROM projects WHERE id=%s", (withdraw_project,))
        withdraw_state = cursor.fetchone()
    check("previous submitter cannot withdraw a newer concurrent submission",
          withdraw_state == ("PENDING_CONFIRMATION", "COMPANY"))

    # A view_all user cannot submit; the supplier submitter still receives the
    # result notification even when the reviewer is selected by global scope.
    notice_project = _new_project(client, conn, supplier["id"], old_user["id"], "供应商提交结果通知")
    _insert_file(conn, notice_project, new_user["id"])
    view_all_client.call("POST", f"/api/v1/projects/{notice_project}/submit", {}, expected=403)
    notice_submission = supplier_client.call(
        "POST", f"/api/v1/projects/{notice_project}/submit", {})
    reviewer_pending = reviewer_client.call(
        "GET", "/api/v1/dashboard/pending-projects?page=1&pageSize=100")
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT recipient_user_id FROM email_outbox WHERE project_id=%s "
            "AND event_type='PROJECT_SUBMITTED' ORDER BY id",
            (notice_project,),
        )
        submission_recipients = [row[0] for row in cursor.fetchall()]
    check(
        "supplier submission reaches the active global reviewer and dashboard task",
        reviewer_user["id"] in submission_recipients
        and any(item["id"] == notice_project for item in reviewer_pending["list"]),
    )
    client.call("POST", f"/api/v1/projects/{notice_project}/confirm", {
        "expectedSubmissionId": notice_submission["latestSubmissionId"],
    })
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT recipient_user_id FROM email_outbox WHERE project_id=%s "
            "AND event_type='PROJECT_CONFIRMED' ORDER BY id",
            (notice_project,),
        )
        recipients = [row[0] for row in cursor.fetchall()]
    check("supplier submitter receives project result notification",
          recipients == [supplier_user["id"]])
