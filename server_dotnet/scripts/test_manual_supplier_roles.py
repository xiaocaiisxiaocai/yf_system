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
    department = admin.call('POST', '/api/v1/admin/departments', {
        'name': '供应商账号管理组织-' + secrets.token_hex(4),
        'parentId': None,
        'sortNo': 0,
    })

    # Deliberately without file:preview so the delegation ceiling can still be observed below.
    account_operator_role = _role(admin, ['supplier:list', 'supplier:account', 'supplier:account_delete', 'role:manage', 'dashboard', 'project:list',
                                          'file:upload', 'file:download', 'message:create'])
    account_operator_password = _password()
    account_operator_employee = 'supplier_accounts_' + secrets.token_hex(4)
    account_operator_user = admin.call('POST', '/api/v1/admin/users', {
        'employeeNo': account_operator_employee,
        'password': account_operator_password,
        'realName': '供应商账号管理员',
        'email': account_operator_employee + '@example.invalid',
        'departmentId': department['id'],
        'roleId': account_operator_role,
    })
    with conn.cursor() as cursor:
        cursor.execute('UPDATE users SET must_change_password=0 WHERE id=%s', (account_operator_user['id'],))
    account_operator = admin.__class__(admin.base)
    account_operator.login(account_operator_employee, account_operator_password)
    visible_suppliers = account_operator.call('GET', '/api/v1/admin/suppliers?pageSize=100')
    account_operator.call('GET', f"/api/v1/admin/suppliers/{supplier['id']}")
    account_operator.call('GET', f"/api/v1/admin/suppliers/{supplier['id']}/accounts")
    account_operator.call('POST', '/api/v1/admin/suppliers', {'name': '越权供应商'}, expected=403)
    account_operator.call('PUT', f"/api/v1/admin/suppliers/{supplier['id']}", {'name': '越权改名'}, expected=403)
    check('supplier account permission can read suppliers and manage accounts without supplier mutation authority',
          any(row['id'] == supplier['id'] for row in visible_suppliers['list']))

    # Submit/withdraw are supplier-only, so an internal operator may grant them without holding them;
    # every other code in the supplier role must still be within the operator's own permissions.
    workflow_role = _role(admin, ['dashboard', 'project:list', 'file:upload', 'file:download', 'message:create',
                                  'project:submit', 'project:withdraw'])
    beyond_operator = _role(admin, ['dashboard', 'project:list', 'file:preview', 'project:submit'])
    operator_options = {role['id'] for role in account_operator.call('GET', '/api/v1/admin/supplier-role-options')}
    check('supplier role options ignore supplier-only codes but keep the delegation ceiling',
          workflow_role in operator_options and beyond_operator not in operator_options)

    def operator_create(role_id, expected=200):
        return account_operator.call('POST', f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
            'employeeNo': 'delegated_' + secrets.token_hex(5), 'realName': '委派供应商账号',
            'email': 'delegated@example.invalid', 'password': _password(), 'roleId': role_id,
        }, expected=expected)

    delegated = operator_create(workflow_role)
    operator_create(beyond_operator, expected=403)
    permission_ids = {item['code']: item['id'] for item in account_operator.call('GET', '/api/v1/permissions')}
    account_operator.call('PUT', f'/api/v1/admin/roles/{workflow_role}/permissions', {
        'permissionIds': [permission_ids[code] for code in (
            'dashboard', 'project:list', 'file:upload', 'file:download', 'message:create',
            'project:submit', 'project:withdraw')],
    })
    account_operator.call('PUT', f"/api/v1/admin/supplier-accounts/{delegated['id']}/status", {'status': 'DISABLED'})
    reenabled = account_operator.call('PUT', f"/api/v1/admin/supplier-accounts/{delegated['id']}/status", {'status': 'ACTIVE'})
    check('supplier account operator can create and re-enable accounts holding submit/withdraw',
          delegated['roleId'] == workflow_role and reenabled['status'] == 'ACTIVE')

    protected_password = _password()
    protected_employee = 'protected_' + secrets.token_hex(5)
    protected = admin.call('POST', f"/api/v1/admin/suppliers/{supplier['id']}/accounts", {
        'employeeNo': protected_employee, 'realName': '高权限供应商账号',
        'email': 'protected@example.invalid', 'password': protected_password, 'roleId': beyond_operator,
    })
    with conn.cursor() as cursor:
        cursor.execute('SELECT password_hash,status FROM users WHERE id=%s', (protected['id'],))
        protected_hash, protected_status = cursor.fetchone()
    account_operator.call('PUT', f"/api/v1/admin/supplier-accounts/{protected['id']}", {
        'realName': '越权修改', 'email': 'changed@example.invalid',
    }, expected=403)
    account_operator.call('PUT', f"/api/v1/admin/supplier-accounts/{protected['id']}/status",
                          {'status': 'DISABLED'}, expected=403)
    account_operator.call('PUT', f"/api/v1/admin/supplier-accounts/{protected['id']}/password",
                          {'newPassword': _password()}, expected=403)
    account_operator.call('DELETE', f"/api/v1/admin/supplier-accounts/{protected['id']}", expected=403)
    with conn.cursor() as cursor:
        cursor.execute('SELECT real_name,email,password_hash,status FROM users WHERE id=%s', (protected['id'],))
        protected_after = cursor.fetchone()
        cursor.execute('SELECT role_id FROM user_roles WHERE user_id=%s', (protected['id'],))
        protected_role_after = cursor.fetchone()[0]
    check('supplier account mutations enforce the target role delegation ceiling atomically',
          protected_after == ('高权限供应商账号', 'protected@example.invalid', protected_hash, protected_status)
          and protected_role_after == beyond_operator)

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
        roles_before_missing_default = admin.call('GET', '/api/v1/admin/roles?pageSize=100')
        create(expected=400)
        roles_after_missing_default = admin.call('GET', '/api/v1/admin/roles?pageSize=100')
        check('missing default supplier role rejects account creation without recreating roles',
              roles_after_missing_default['total'] == roles_before_missing_default['total']
              and {role['id'] for role in roles_after_missing_default['list']}
              == {role['id'] for role in roles_before_missing_default['list']})
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
    safe_role_view = next(role for role in admin.call('GET', '/api/v1/admin/roles?pageSize=100')['list'] if role['id'] == safe)
    check('supplier account creation persists its selected custom role',
          account['roleId'] == safe == saved['roleId'] and saved['roleName']
          and safe_role_view['supplierRestricted'] is True)
    admin.call('PUT', f"/api/v1/admin/supplier-accounts/{account['id']}/status", {'status': 'DISABLED'})
    permissions = {item['code']: item['id'] for item in admin.call('GET', '/api/v1/permissions')}
    admin.call('PUT', f'/api/v1/admin/roles/{safe}/permissions', {'permissionIds': [permissions['role:manage']]}, expected=400)
    admin.call('PUT', f'/api/v1/admin/roles/{safe}/status', {'status': 'DISABLED'})
    admin.call('PUT', f"/api/v1/admin/supplier-accounts/{account['id']}/status", {'status': 'ACTIVE'}, expected=400)
    admin.call('PUT', f'/api/v1/admin/roles/{safe}/status', {'status': 'ACTIVE'})
    active = admin.call('PUT', f"/api/v1/admin/supplier-accounts/{account['id']}/status", {'status': 'ACTIVE'})
    check('supplier roles cannot gain admin privileges and activation requires a safe active role', active['status'] == 'ACTIVE')
