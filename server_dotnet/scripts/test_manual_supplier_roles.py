"""Custom supplier-role checks; caller owns the disposable database and API."""
import secrets
from test_collaboration_contracts import _role, _password
from test_role_fixtures import _require_disposable_database


def run_manual_supplier_role_checks(admin, conn, check):
    _require_disposable_database(conn)
    roles = admin.call('GET', '/api/v1/admin/roles?pageSize=100')['list']
    built_in = next(role for role in roles if role['name'] == '供应商人员' and role['isBuiltIn'])
    root_role = next(role for role in roles if role['name'] == '系统管理员' and role['isBuiltIn'])
    supplier = admin.call('POST', '/api/v1/admin/suppliers', {'name': '手工角色-' + secrets.token_hex(4)})

    def create(role_id=None, expected=200):
        body = {'employeeNo': 'manual_' + secrets.token_hex(5), 'realName': '手工角色测试',
                'email': 'manual@example.invalid', 'password': _password()}
        if role_id is not None: body['roleId'] = role_id
        return admin.call('POST', f"/api/v1/admin/suppliers/{supplier['id']}/accounts", body, expected=expected)

    # Simulate the new-installation missing-default case without destroying fixtures
    # needed by the broader regression suite. Restore its metadata in finally.
    with conn.cursor() as cursor:
        cursor.execute('UPDATE roles SET is_built_in=0 WHERE id=%s', (built_in['id'],))
    try:
        create(expected=400)
        check('missing default supplier role rejects account creation without recreating roles',
              admin.call('GET', '/api/v1/admin/roles?pageSize=100')['total'] == len(roles))
    finally:
        with conn.cursor() as cursor:
            cursor.execute('UPDATE roles SET is_built_in=1 WHERE id=%s', (built_in['id'],))
    safe = _role(admin, ['dashboard', 'project:list'])
    unsafe = _role(admin, ['role:manage'])
    options = admin.call('GET', '/api/v1/admin/supplier-role-options')
    ids = {role['id'] for role in options}
    check('supplier role options include safe custom roles and exclude administration roles',
          safe in ids and unsafe not in ids and root_role['id'] not in ids)
    create(unsafe, expected=400)
    create(root_role['id'], expected=400)
    account = create(safe)
    saved = next(row for row in admin.call('GET', f"/api/v1/admin/suppliers/{supplier['id']}/accounts") if row['id'] == account['id'])
    check('supplier account creation persists its selected custom role',
          account['roleId'] == safe == saved['roleId'] and saved['roleName'])
    admin.call('PUT', f"/api/v1/admin/supplier-accounts/{account['id']}/status", {'status': 'DISABLED'})
    permissions = {item['code']: item['id'] for item in admin.call('GET', '/api/v1/permissions')}
    admin.call('PUT', f'/api/v1/admin/roles/{safe}/permissions', {'permissionIds': [permissions['role:manage']]}, expected=400)
    admin.call('PUT', f'/api/v1/admin/roles/{safe}/status', {'status': 'DISABLED'})
    admin.call('PUT', f"/api/v1/admin/supplier-accounts/{account['id']}/status", {'status': 'ACTIVE'}, expected=400)
    admin.call('PUT', f'/api/v1/admin/roles/{safe}/status', {'status': 'ACTIVE'})
    active = admin.call('PUT', f"/api/v1/admin/supplier-accounts/{account['id']}/status", {'status': 'ACTIVE'})
    check('supplier roles cannot gain admin privileges and activation requires a safe active role', active['status'] == 'ACTIVE')
