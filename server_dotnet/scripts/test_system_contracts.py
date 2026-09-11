"""System config and audit integrity checks using caller-owned isolated resources."""
from urllib.parse import urlencode

def run_system_checks(client, conn, check):
    before = {x["key"]: x["value"] for x in client.call("GET", "/api/v1/admin/system/configs")}
    client.call("PUT", "/api/v1/admin/system/configs", {"items": [
        {"key": "notify.enabled", "value": "false"}, {"key": "unknown-test-setting", "value": "anything"}
    ]}, expected=400)
    after = {x["key"]: x["value"] for x in client.call("GET", "/api/v1/admin/system/configs")}
    check("system config batch rejects unknown key atomically", before == after)
    client.call("PUT", "/api/v1/admin/system/configs", {"items": [
        {"key": "notify.enabled", "value": "true"}, {"key": "notify.enabled", "value": "false"}
    ]}, expected=400)
    check("system config rejects duplicate keys", True)
    with conn.cursor() as cursor:
        cursor.execute("SELECT cfg_value FROM system_configs WHERE cfg_key='security.management_lock'")
        original_gate = cursor.fetchone()[0]
    for key in ("SECURITY.MANAGEMENT_LOCK", "security.management_lock ", "security.management_lóck"):
        client.call("PUT", "/api/v1/admin/system/configs", {"items": [
            {"key": key, "value": "must-not-be-written"}
        ]}, expected=400)
    client.call("PUT", "/api/v1/admin/system/configs", {"items": [
        {"key": "notify.enabled", "value": "true"}, {"key": " NOTIFY.ENABLED ", "value": "false"}
    ]}, expected=400)
    client.call("PUT", "/api/v1/admin/system/configs", {"items": [
        {"key": "UPLOAD.CHUNK_SIZE", "value": "0"}
    ]}, expected=400)
    with conn.cursor() as cursor:
        cursor.execute("SELECT cfg_value FROM system_configs WHERE cfg_key='security.management_lock'")
        unchanged_gate = cursor.fetchone()[0] == original_gate
    final_configs = {x["key"]: x["value"] for x in client.call("GET", "/api/v1/admin/system/configs")}
    check("config aliases cannot bypass validation or change internal state", unchanged_gate and final_configs == before)
    with conn.cursor() as cursor:
        cursor.execute("INSERT INTO audit_logs(action,target_type,target_id,detail,created_at) VALUES('DOTNET_TEST','test','42',%s,UTC_TIMESTAMP())", ('{"reason":"owned fixture"}',))
        aid = cursor.lastrowid
    filtered = client.call("GET", "/api/v1/admin/audit-logs?action=DOTNET_TEST&targetType=test&targetId=42")
    check("audit filters and JSON detail contract", filtered["total"] == 1 and filtered["list"][0]["detail"]["reason"] == "owned fixture")
    padded = client.call("GET", "/api/v1/admin/audit-logs?" + urlencode({
        "action": " DOTNET_TEST ", "targetType": " test ", "targetId": " 42 ", "keyword": " DOTNET_TEST "
    }))
    check("audit filters normalize pasted surrounding whitespace", padded["total"] == 1 and padded["list"][0]["id"] == aid)
    categorized = client.call("GET", "/api/v1/admin/audit-logs?" + urlencode({
        "action": "DOTNET_TEST", "category": " AUTH "
    }))
    check("audit category trims whitespace without widening results", categorized["total"] == 0)
    deleted = client.call("POST", "/api/v1/admin/audit-logs/batch-delete", {"ids": [aid, aid]})
    check("audit deletion deduplicates IDs", deleted["deleted"] == 1)
    protected = client.call("GET", "/api/v1/admin/audit-logs?action=AUDIT_LOG_DELETE")
    client.call("DELETE", f"/api/v1/admin/audit-logs/{protected['list'][0]['id']}", expected=403)
    check("audit cleanup record cannot be deleted", True)
