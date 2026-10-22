/** 其他标签页改写 yf-auth 后本页应做的事。 */
export type AuthStorageAction =
  | { type: 'ignore' }
  | { type: 'logout' }
  | { type: 'reload' }
  | { type: 'apply'; permissions: string[]; menus: string[]; mustChangePassword: boolean }

interface CurrentAuth {
  user: { id: number } | null
  permissions: string[]
  menus: string[]
  mustChangePassword: boolean
}

function stringList(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []
}

function sameSet(left: string[], right: string[]) {
  if (left.length !== right.length) return false
  const sorted = [...right].sort()
  return [...left].sort().every((item, index) => item === sorted[index])
}

/**
 * 其他标签页登出：本页也登出。换了账号：整页重载（本页的访问令牌属于旧账号）。
 * 同一账号的权限或菜单变化：直接套用到本页，不重载，避免丢失未保存的表单；只是顺序不同则忽略。
 */
export function decideAuthStorageSync(newValue: string | null, current: CurrentAuth): AuthStorageAction {
  let nextState: Record<string, unknown> | null
  try {
    const parsed: unknown = newValue ? JSON.parse(newValue) : null
    const state = parsed && typeof parsed === 'object' ? (parsed as { state?: unknown }).state : null
    nextState = state && typeof state === 'object' ? state as Record<string, unknown> : null
  } catch {
    return { type: 'ignore' }
  }
  const nextUser = nextState?.user as { id?: unknown } | null | undefined
  if (!current.user) return { type: 'ignore' }
  if (!nextUser) return { type: 'logout' }
  if (nextUser.id !== current.user.id) return { type: 'reload' }
  const permissions = stringList(nextState?.permissions)
  const menus = stringList(nextState?.menus)
  const mustChangePassword = nextState?.mustChangePassword === true
  if (sameSet(permissions, current.permissions) && sameSet(menus, current.menus)
    && mustChangePassword === current.mustChangePassword) return { type: 'ignore' }
  return { type: 'apply', permissions, menus, mustChangePassword }
}
