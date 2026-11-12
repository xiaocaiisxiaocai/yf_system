import { OEM_UI_ENABLED } from '../features'
import { oemHome } from '../oem/oemNavigation'
import { outsourceHome } from '../outsource/outsourceNavigation'

interface NavigationGrants {
  menus: readonly string[]
  permissions: readonly string[]
  user: { userType: string } | null
}

// Keep section visibility and the default landing page on the same access rules.
export const COLLABORATION_PAGES = [
  { code: 'dashboard', path: '/', label: '工作台', anyPermission: ['project:list'] },
  { code: 'project:list', path: '/projects', label: 'Robot仿真协作', anyPermission: ['project:list'] },
  { code: 'supplier:list', path: '/suppliers', label: '供应商管理', anyPermission: ['supplier:manage', 'supplier:account'] },
  { code: 'org:user', path: '/org/users', label: '用户管理', anyPermission: ['user:manage'] },
  { code: 'org:dept', path: '/org/depts', label: '组织架构', anyPermission: [] },
  { code: 'rbac:role', path: '/rbac/roles', label: '角色与权限', anyPermission: ['role:manage'] },
  { code: 'log:audit', path: '/logs', label: '操作日志', anyPermission: ['log:view'] },
  { code: 'system:config', path: '/system/config', label: '系统参数', anyPermission: ['config:manage'] },
  { code: 'system:dict', path: '/system/dictionaries', label: '数据字典', anyPermission: ['dict:manage'] },
] as const

export function collaborationPages({ menus, permissions, user }: NavigationGrants) {
  return COLLABORATION_PAGES.filter(page =>
    (page.code === 'org:dept' && user?.userType === 'INTERNAL' && permissions.includes('dept:leader_manage'))
    || (menus.includes(page.code) && (page.anyPermission.length === 0
      || page.anyPermission.some(permission => permissions.includes(permission)))))
}

export function businessHomes(grants: NavigationGrants) {
  const internal = grants.user?.userType === 'INTERNAL'
  return {
    collaboration: collaborationPages(grants)[0]?.path ?? null,
    oem: OEM_UI_ENABLED && internal ? oemHome(grants.permissions) : null,
    outsource: internal ? outsourceHome(grants.permissions) : null,
  }
}
