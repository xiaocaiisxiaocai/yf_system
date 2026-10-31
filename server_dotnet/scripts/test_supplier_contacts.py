"""HTTP contract for supplier contacts, Robot-owner selection, typed signatures and contact mail.

Owned by test-isolated.py; see 厂商联络人与签名契约-2026-10-30.
"""

import secrets

from test_business_acceptance import _create_started_project, _upload_chunks
from test_project_remediation import _new_supplier
from upload_contract import init_request


def run_supplier_contact_checks(client, Client, conn, check):
    supplier, supplier_user, supplier_client = _new_supplier(client, Client, conn)
    sid = supplier["id"]
    project_id = _create_started_project(client, client, conn, sid, "联络人签名项目-" + secrets.token_hex(4))

    zhang = client.call("POST", f"/api/v1/admin/suppliers/{sid}/contacts", {
        "name": "张三", "email": "zhang-" + secrets.token_hex(3) + "@example.invalid", "sortNo": 1,
    })
    li = client.call("POST", f"/api/v1/admin/suppliers/{sid}/contacts", {
        "name": "李四", "email": "li-" + secrets.token_hex(3) + "@example.invalid", "sortNo": 2,
    })
    client.call("POST", f"/api/v1/admin/suppliers/{sid}/contacts", {
        "name": "张三", "email": "dup@example.invalid",
    }, expected=409)
    supplier_client.call("GET", f"/api/v1/admin/suppliers/{sid}/contacts", expected=403)
    supplier_client.call("GET", f"/api/v1/supplier-contacts?supplierId={sid}", expected=403)
    options = client.call("GET", f"/api/v1/supplier-contacts?supplierId={sid}")
    names = [item["name"] for item in options if item["enabled"]]
    check("internal users list the supplier's contacts for Robot owners; suppliers cannot",
          names[:2] == ["张三", "李四"] and all(set(item) == {"id", "name", "enabled"} for item in options))

    group = client.call("GET", f"/api/v1/projects/{project_id}")
    detail = client.call("GET", f"/api/v1/project-groups/{group['projectGroupId']}")["group"]
    updated = client.call("PUT", f"/api/v1/project-groups/{detail['id']}", {
        "name": detail["name"], "description": detail["description"], "supplierId": sid,
        "workOrderNos": detail["workOrderNos"], "machineModel": detail["machineModel"],
        "robotPartId": detail["robotPartId"], "priorityId": detail["priorityId"],
        "robotTypeId": detail["robotTypeId"], "robotOwnerContactIds": [li["id"], zhang["id"]],
        "expectedCompletionDate": detail["expectedCompletionDate"], "expectedUpdatedAt": detail["updatedAt"],
    })
    check("main projects store several Robot owners and their joined-name snapshot",
          updated["robotOwnerContactIds"] == [li["id"], zhang["id"]]
          and updated["robotOwnerName"] == "李四、张三"
          and client.call("GET", f"/api/v1/projects/{project_id}")["robotOwnerName"] == "李四、张三")

    unsigned = supplier_client.call("POST", f"/api/v1/projects/{project_id}/messages",
                                    {"content": "未签名"}, expected=400)
    stranger = supplier_client.call("POST", f"/api/v1/projects/{project_id}/messages",
                                    {"content": "冒名", "signerName": "王五"}, expected=400)
    signed = supplier_client.call("POST", f"/api/v1/projects/{project_id}/messages",
                                  {"content": "张三的说明", "signerName": " 张三 "})
    check("supplier messages carry a typed signature checked against the contacts",
          unsigned["message"] == "请填写签名（提供人姓名）"
          and "不是该供应商登记的联络人" in stranger["message"]
          and signed["signerName"] == "张三")

    payload = b"supplier signed upload"
    supplier_client.call("POST", "/api/v1/uploads/init",
                         init_request(project_id, "unsigned.pdf", payload), expected=400)
    file_id, _, _ = _upload_chunks(supplier_client, project_id, "signed.pdf", payload, signer_name="李四")
    files = client.call("GET", f"/api/v1/projects/{project_id}/files?keyword=signed.pdf&page=1&pageSize=10")
    check("supplier uploads carry the typed signature into the file list",
          [(item["id"], item["signerName"]) for item in files["list"]] == [(file_id, "李四")])

    client.call("POST", f"/api/v1/projects/{project_id}/messages", {"content": "内部回复"})
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT recipient_user_id,recipient_email FROM email_outbox "
            "WHERE project_id=%s AND event_type='MESSAGE_CREATED' ORDER BY id",
            (project_id,),
        )
        rows = cursor.fetchall()
    check("supplier mail goes to every active contact instead of the shared account address",
          sorted(email for user_id, email in rows if user_id == supplier_user["id"])
          == sorted([zhang["email"], li["email"]]))

    # Disabled contacts can no longer sign; with none active the supplier works unsigned again.
    for contact in (zhang, li):
        client.call("PUT", f"/api/v1/admin/supplier-contacts/{contact['id']}/status", {"status": "DISABLED"})
    supplier_client.call("POST", f"/api/v1/projects/{project_id}/messages",
                         {"content": "停用签名", "signerName": "张三"}, expected=400)
    plain = supplier_client.call("POST", f"/api/v1/projects/{project_id}/messages", {"content": "无联络人时可不签名"})
    check("suppliers without active contacts send unsigned messages", plain["signerName"] is None)
