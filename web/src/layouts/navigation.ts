import { OEM_UI_ENABLED } from '../features'
import { oemHome } from '../oem/oemNavigation'
import { outsourceHome } from '../outsource/outsourceNavigation'

interface NavigationGrants {
  menus: readonly string[]
  permissions: readonly string[]
  user: { userType: string } | null
}

// Single source of the collaboration pages' access rules: menu visibility, the default landing page and the
// route guards in App.tsx (via pageGuard) all read this list, so a page cannot be visible but forbidden or vice versa.
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

export type CollaborationPageCode = (typeof COLLABORATION_PAGES)[number]['code']

/** 内部主管维护权限：没有组织架构菜单也能进入组织架构维护主管。 */
export function canManageLeaders({ permissions, user }: Pick<NavigationGrants, 'permissions' | 'user'>) {
  return user?.userType === 'INTERNAL' && permissions.includes('dept:leader_manage')
}

/**
 * 页面路由守卫的条件，与菜单共用 COLLABORATION_PAGES 这一份规则：需要该菜单，并持有任一所需操作权限。
 * 工作台（首页）由 Home 按同一规则决定落点，不走这里。
 */
export function pageGuard(code: Exclude<CollaborationPageCode, 'dashboard'>): { menu: string; anyPermission?: string[] } {
  const page = COLLABORATION_PAGES.find(item => item.code === code)!
  return { menu: page.code, anyPermission: page.anyPermission.length > 0 ? [...page.anyPermission] : undefined }
}

export function collaborationPages({ menus, permissions, user }: NavigationGrants) {
  return COLLABORATION_PAGES.filter(page =>
    (page.code === 'org:dept' && canManageLeaders({ permissions, user }))
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
