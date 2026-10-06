"""HTTP authorization and recently added route contracts on owned fixtures."""

import json
from pathlib import Path
import re
import secrets

from test_business_acceptance import _create_project_group
from test_project_remediation import _new_internal, _new_role, _new_supplier, _section


ROOT = Path(__file__).resolve().parents[2]
PUBLIC_OPERATIONS = {
    ("POST", "/api/v1/auth/login"),
    ("POST", "/api/v1/auth/logout"),
    ("POST", "/api/v1/auth/refresh"),
    ("POST", "/api/v1/oem/auth/login"),
    ("POST", "/api/v1/oem/auth/logout"),
    ("POST", "/api/v1/oem/auth/refresh"),
}
HTTP_METHODS = {"get", "post", "put", "delete", "patch"}


def _concrete_path(path):
    values = {
        "id": "1",
        "groupId": "1",
        "jobId": "1",
        "copyId": "1",
        "messageId": "1",
        "imageId": "1",
        "sessionId": "00000000-0000-0000-0000-000000000001",
        "index": "0",
        "handle": "invalid-handle",
    }
    return re.sub(r"\{([^}]+)\}", lambda match: values.get(match.group(1), "1"), path)


def run_anonymous_route_contracts(anonymous, check):
    contract = json.loads(
        (ROOT / "server_dotnet/tests/Contracts/openapi-v1.json").read_text(encoding="utf-8")
    )
    checked = 0
    for path, operations in contract["paths"].items():
        for method in operations:
            if method not in HTTP_METHODS or (method.upper(), path) in PUBLIC_OPERATIONS:
                continue
            response = anonymous.call(method.upper(), _concrete_path(path), expected=401)
            if response.get("code") not in {40101, 40102}:
                raise AssertionError(f"anonymous route did not return the auth contract: {method} {path}")
            checked += 1
    check("all OpenAPI operations outside the public auth allowlist reject anonymous access", checked > 0)


def run_recent_route_contracts(admin, Client, conn, check):
    suffix = secrets.token_hex(5)
    section_id = _section(admin, suffix)
    role_id = _new_role(admin, ["project:list", "project:create", "message:create"])
    actor_user, actor = _new_internal(admin, Client, conn, role_id, "路由合同成员", section_id)
    transfer_role_id = _new_role(
        admin, ["project:list", "project:view_all", "project:transfer"]
    )
    transfer_user, transfer_actor = _new_internal(
        admin, Client, conn, transfer_role_id, "路由合同转移员", section_id
    )
    supplier, _, supplier_actor = _new_supplier(admin, Client, conn)
    group, project = _create_project_group(
        actor, admin, conn, supplier["id"], "路由合同项目-" + suffix
    )
    project_id = project["id"]

    actor.call("GET", "/api/v1/project-owner-options", expected=403)
    owners = transfer_actor.call("GET", "/api/v1/project-owner-options")
    supplier_actor.call("GET", "/api/v1/project-owner-options", expected=403)
    check("project owner options require transfer permission and reject supplier accounts",
          any(item["id"] == actor_user["id"] for item in owners))

    actor.call(
        "PUT",
        f"/api/v1/project-groups/{group['id']}/responsible",
        {"responsibleUserId": actor_user["id"]},
        expected=403,
    )
    transferred = transfer_actor.call(
        "PUT",
        f"/api/v1/project-groups/{group['id']}/responsible",
        {"responsibleUserId": transfer_user["id"]},
    )
    check("responsible transfer requires transfer permission at the HTTP boundary",
          transferred["responsibleUserId"] == transfer_user["id"])

    dictionary = admin.call("POST", "/api/v1/project-dictionaries", {
        "type": "PRIORITY",
        "name": "路由合同优先级-" + suffix,
        "parentId": None,
        "sortNo": 9876,
        "enabled": True,
    })
    updated_payload = {
        "type": "PRIORITY",
        "name": dictionary["name"] + "-更新",
        "parentId": None,
        "sortNo": 9877,
        "enabled": False,
    }
    actor.call("PUT", f"/api/v1/project-dictionaries/{dictionary['id']}", updated_payload, expected=403)
    updated = admin.call("PUT", f"/api/v1/project-dictionaries/{dictionary['id']}", updated_payload)
    actor.call("DELETE", f"/api/v1/project-dictionaries/{dictionary['id']}", expected=403)
    deleted = admin.call("DELETE", f"/api/v1/project-dictionaries/{dictionary['id']}")
    check("dictionary update and delete require dict management permission",
          updated["name"] == updated_payload["name"] and not updated["enabled"]
          and deleted == {})

    actor.call("GET", "/api/v1/robot-part-supplier-options", expected=403)
    supplier_options = admin.call("GET", "/api/v1/robot-part-supplier-options")
    check("robot-part supplier options require dict management permission",
          any(item["id"] == supplier["id"] for item in supplier_options))

    message = actor.call("POST", f"/api/v1/projects/{project_id}/messages", {
        "content": "路由合同消息-" + suffix,
    })
    receipts = actor.call(
        "GET", f"/api/v1/projects/{project_id}/message-receipts?ids={message['id']}"
    )
    check("project message receipt summary is available to a project participant",
          len(receipts) == 1 and receipts[0]["id"] == message["id"])

    history = actor.call("GET", f"/api/v1/projects/{project_id}/copy-history")
    check("project copy history route returns an empty typed history for a new owned project",
          history["source"] is None and history["copies"] == [])

    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT id,source_project_id FROM project_copies ORDER BY id DESC LIMIT 1"
        )
        latest_copy = cursor.fetchone()
    if latest_copy is None:
        raise AssertionError("background copy fixture must run before recent route contracts")
    copy_id, source_project_id = latest_copy
    source_history = admin.call("GET", f"/api/v1/projects/{source_project_id}/copy-history")
    copy_files = admin.call("GET", f"/api/v1/project-copies/{copy_id}/files?page=1&pageSize=20")
    actor.call("GET", f"/api/v1/project-copies/{copy_id}/files?page=1&pageSize=20", expected=404)
    check("copy history and file mappings are exposed only when both projects are visible",
          any(item["copyId"] == copy_id for item in source_history["copies"])
          and copy_files["total"] >= 1)
