"""Focused project workflow regressions using caller-owned isolated resources.

The caller supplies the already authenticated administrator client, disposable
database connection, and API Client type. The mail worker must remain disabled.
"""

import secrets
import threading
import time


def _password():
    return "Project-" + secrets.token_urlsafe(18)


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


def _new_internal(client, Client, conn, role_id, label):
    employee = "project_" + secrets.token_hex(5)
    password = _password()
    user = client.call("POST", "/api/v1/admin/users", {
        "employeeNo": employee,
        "password": password,
        "realName": label,
        "email": employee + "@example.invalid",
        "departmentId": None,
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


def _new_project(client, supplier_id, name):
    project = client.call("POST", "/api/v1/projects", {
        "name": name + "-" + secrets.token_hex(5),
        "description": "owned isolated project remediation fixture",
        "supplierId": supplier_id,
    })
    client.call("PUT", f"/api/v1/projects/{project['id']}/status", {"status": "IN_PROGRESS"})
    return project["id"]


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
    supplier, supplier_user, supplier_client = _new_supplier(client, Client, conn)
    role_id = _new_role(client, ["project:submit", "project:withdraw"])
    view_all_role_id = _new_role(client, ["project:view_all", "project:submit"])
    old_user, old_client = _new_internal(client, Client, conn, role_id, "旧提交者")
    new_user, _ = _new_internal(client, Client, conn, role_id, "新提交者")
    view_all_user, view_all_client = _new_internal(
        client, Client, conn, view_all_role_id, "非成员提交者")

    # The operator is mandatory and counts against the persisted 200-member
    # limit. Rejection must happen before validating or writing requested users.
    member_project = _new_project(client, supplier["id"], "成员最终上限")
    with conn.cursor() as cursor:
        cursor.execute("SELECT MAX(id) FROM users")
        first_unused_id = cursor.fetchone()[0] + 1000
        cursor.execute(
            "SELECT COUNT(*) FROM project_members WHERE project_id=%s",
            (member_project,),
        )
        members_before = cursor.fetchone()[0]
    client.call("PUT", f"/api/v1/projects/{member_project}/members", {
        "userIds": list(range(first_unused_id, first_unused_id + 200)),
    }, expected=400)
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT COUNT(*) FROM project_members WHERE project_id=%s",
            (member_project,),
        )
        members_after = cursor.fetchone()[0]
    check("required operator counts against project member limit",
          members_before == members_after == 1)

    # A submit waiting behind the project's row lock must see a just-committed
    # deletion of the final file. Under the former RR snapshot it incorrectly
    # entered PENDING_CONFIRMATION.
    deleted_project = _new_project(client, supplier["id"], "并发删除文件")
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
        submit_client.token = client.token
        worker, result = _request_in_thread(
            submit_client, "POST", f"/api/v1/projects/{deleted_project}/submit",
            {"confirmSide": "SUPPLIER"}, 409,
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
    upload_project = _new_project(client, supplier["id"], "并发活动上传")
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
        submit_client.token = client.token
        worker, result = _request_in_thread(
            submit_client, "POST", f"/api/v1/projects/{upload_project}/submit",
            {"confirmSide": "SUPPLIER"}, 409,
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
    withdraw_project = _new_project(client, supplier["id"], "并发旧提交者撤回")
    _insert_file(conn, withdraw_project, old_user["id"])
    client.call("PUT", f"/api/v1/projects/{withdraw_project}/members", {
        "userIds": [old_user["id"], new_user["id"]],
    })
    old_client.call("POST", f"/api/v1/projects/{withdraw_project}/submit", {"confirmSide": "SUPPLIER"})
    supplier_client.call("POST", f"/api/v1/projects/{withdraw_project}/reject", {"reason": "建立旧提交历史"})
    conn.begin()
    try:
        with conn.cursor() as cursor:
            cursor.execute("SELECT id FROM projects WHERE id=%s FOR UPDATE", (withdraw_project,))
            cursor.execute(
                "UPDATE projects SET status='PENDING_CONFIRMATION',confirm_side='SUPPLIER',"
                "updated_at=UTC_TIMESTAMP(3) WHERE id=%s",
                (withdraw_project,),
            )
            cursor.execute(
                "INSERT INTO project_status_logs(project_id,from_status,to_status,action,operator_id,"
                "confirm_side,reason,created_at) VALUES(%s,'IN_PROGRESS','PENDING_CONFIRMATION','SUBMIT',"
                "%s,'SUPPLIER',NULL,UTC_TIMESTAMP(3))",
                (withdraw_project, new_user["id"]),
            )
        worker, result = _request_in_thread(
            old_client, "POST", f"/api/v1/projects/{withdraw_project}/withdraw", None, 403,
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
          withdraw_state == ("PENDING_CONFIRMATION", "SUPPLIER"))

    # A view_all user may submit without becoming a project member; the explicit
    # latest submitter still has to receive the result notification.
    notice_project = _new_project(client, supplier["id"], "非成员提交结果通知")
    _insert_file(conn, notice_project, new_user["id"])
    with conn.cursor() as cursor:
        cursor.execute("DELETE FROM project_members WHERE project_id=%s", (notice_project,))
        cursor.execute(
            "UPDATE projects SET created_by=%s WHERE id=%s",
            (new_user["id"], notice_project),
        )
        cursor.execute(
            "INSERT INTO project_members(project_id,user_id,created_by,created_at) "
            "VALUES(%s,%s,%s,UTC_TIMESTAMP(3))",
            (notice_project, new_user["id"], new_user["id"]),
        )
    view_all_client.call(
        "POST", f"/api/v1/projects/{notice_project}/submit", {"confirmSide": "SUPPLIER"})
    supplier_client.call("POST", f"/api/v1/projects/{notice_project}/confirm")
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT recipient_user_id FROM email_outbox WHERE project_id=%s "
            "AND event_type='PROJECT_CONFIRMED' ORDER BY id",
            (notice_project,),
        )
        recipients = [row[0] for row in cursor.fetchall()]
    check("view_all nonmember submitter receives project result notification",
          recipients == [view_all_user["id"]])
