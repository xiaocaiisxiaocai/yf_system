"""System config and audit integrity checks using caller-owned isolated resources."""
from urllib.parse import urlencode
import json
import secrets

def run_system_checks(client, conn, check):
    with conn.cursor() as cursor:
        cursor.execute("INSERT INTO system_configs(cfg_key,cfg_value,description,updated_at) VALUES('storage.warn_percent','85','legacy fixture',UTC_TIMESTAMP()) ON DUPLICATE KEY UPDATE cfg_value='85'")
    assert all(item['key'] != 'storage.warn_percent' for item in client.call('GET', '/api/v1/admin/system/configs'))
    client.call('PUT', '/api/v1/admin/system/configs', {'items': [{'key': ' STORAGE.WARN_PERCENT ', 'value': '90'}]}, expected=400)
    client.call('GET', '/api/v1/admin/system/storage', expected=404)
    check('retired storage status and warning threshold are no longer exposed or editable', True)
    # This caller always owns a disposable database and runs with WorkerEnabled=false.
    smtp_before = client.call('GET', '/api/v1/admin/system/mail-settings')
    assert 'password' not in smtp_before and not smtp_before['hasPassword']
    anonymous = type(client)(client.base)
    anonymous.call('GET', '/api/v1/admin/system/mail-settings', expected=401)
    authorization_code = secrets.token_urlsafe(18)
    smtp_payload = {'host': 'smtp.example.invalid', 'port': 465, 'username': 'sender@example.invalid',
                    'from': 'notice@example.invalid', 'security': 'SslOnConnect', 'password': authorization_code}
    anonymous.call('PUT', '/api/v1/admin/system/mail-settings', smtp_payload, expected=401)
    client.call('PUT', '/api/v1/admin/system/mail-settings', {**smtp_payload, 'password': None}, expected=400)
    saved = client.call('PUT', '/api/v1/admin/system/mail-settings', smtp_payload)
    assert saved['hasPassword'] and saved['configured'] and 'password' not in saved
    with conn.cursor() as cursor:
        cursor.execute("SELECT cfg_value FROM system_configs WHERE cfg_key='mail.smtp'")
        encrypted_record = cursor.fetchone()[0]
        cursor.execute("SELECT detail FROM audit_logs WHERE action='CONFIG_UPDATE' ORDER BY id DESC LIMIT 1")
        audit_detail = cursor.fetchone()[0]
    assert authorization_code not in encrypted_record and authorization_code not in str(audit_detail)
    assert json.loads(encrypted_record)['ProtectedPassword'].startswith('v1.')
    readback = client.call('GET', '/api/v1/admin/system/mail-settings')
    assert readback == saved
    assert all(item['key'] != 'mail.smtp' for item in client.call('GET', '/api/v1/admin/system/configs'))
    client.call('PUT', '/api/v1/admin/system/configs', {'items': [{'key': ' MAIL.SMTP ', 'value': '{}'}]}, expected=400)
    kept = client.call('PUT', '/api/v1/admin/system/mail-settings', {**smtp_payload, 'port': 587, 'security': 'StartTls', 'password': ''})
    assert kept['hasPassword'] and kept['port'] == 587
    client.call('PUT', '/api/v1/admin/system/mail-settings', {**smtp_payload, 'host': 'other.example.invalid', 'password': None}, expected=400)
    client.call('PUT', '/api/v1/admin/system/mail-settings', {**smtp_payload, 'username': 'other@example.invalid', 'password': None}, expected=400)
    status = client.call('GET', '/api/v1/admin/system/mail-status')
    assert status['configured'] and status['host'] == smtp_payload['host'] and status['port'] == 587
    for bad in ({'host': 'https://smtp.example.invalid'}, {'port': 0}, {'security': 'None'}, {'from': 'bad address'}):
        client.call('PUT', '/api/v1/admin/system/mail-settings', {**smtp_payload, **bad}, expected=400)
    assert client.call('GET', '/api/v1/admin/system/mail-settings') == kept
    check('SMTP settings persist encrypted, redact credentials, preserve blank passwords and become effective without restart', True)
    with conn.cursor() as cursor:
        cursor.execute("DELETE FROM system_configs WHERE cfg_key='mail.smtp'")
    for route in ('users', 'roles', 'suppliers', 'audit-logs'):
        for key in ('page', 'pageSize'):
            for raw in ('', 'abc', '-1', '18446744073709551616', '1&' + key + '=2'):
                rejected = client.call('GET', '/api/v1/admin/' + route + '?' + key + '=' + raw, expected=400)
                assert rejected['code'] == 40001 and 'list' not in rejected
    for raw in ('', 'abc', '-1', '18446744073709551616', '1&departmentId=2'):
        rejected = client.call('GET', '/api/v1/admin/users?departmentId=' + raw, expected=400)
        assert rejected['code'] == 40001 and 'list' not in rejected
    check('malformed numeric admin filters reject without returning broader data', True)
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
    check("audit filters and JSON detail contract", filtered["total"] == 1
          and filtered["list"][0]["detail"]["reason"] == "owned fixture"
          and "canDelete" not in filtered["list"][0])
    padded = client.call("GET", "/api/v1/admin/audit-logs?" + urlencode({
        "action": " DOTNET_TEST ", "targetType": " test ", "targetId": " 42 ", "keyword": " DOTNET_TEST "
    }))
    check("audit filters normalize pasted surrounding whitespace", padded["total"] == 1 and padded["list"][0]["id"] == aid)
    categorized = client.call("GET", "/api/v1/admin/audit-logs?" + urlencode({
        "action": "DOTNET_TEST", "category": " AUTH "
    }))
    check("audit category trims whitespace without widening results", categorized["total"] == 0)
    client.call("POST", "/api/v1/admin/audit-logs/batch-delete", {"ids": [aid]}, expected=404)
    client.call("DELETE", f"/api/v1/admin/audit-logs/{aid}", expected=404)
    with conn.cursor() as cursor:
        cursor.execute("SELECT COUNT(*) FROM audit_logs WHERE id=%s", (aid,))
        preserved = cursor.fetchone()[0]
    check("audit logs cannot be deleted manually", preserved == 1)
