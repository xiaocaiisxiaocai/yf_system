import { lazy, Suspense, useEffect } from 'react'
import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { Result, Spin } from '@arco-design/web-react'
import { useAuth } from './store/auth'
import { bootAuth } from './api/client'

const AdminLayout = lazy(() => import('./layouts/AdminLayout'))
const Login = lazy(() => import('./pages/Login'))
const ChangePassword = lazy(() => import('./pages/ChangePassword'))
const Dashboard = lazy(() => import('./pages/Dashboard'))
const NotFound = lazy(() => import('./pages/NotFound'))
const ProjectList = lazy(() => import('./pages/project/ProjectList'))
const ProjectDetail = lazy(() => import('./pages/project/ProjectDetail'))
const SupplierList = lazy(() => import('./pages/supplier/SupplierList'))
const UserList = lazy(() => import('./pages/org/UserList'))
const DeptManage = lazy(() => import('./pages/org/DeptManage'))
const RoleList = lazy(() => import('./pages/rbac/RoleList'))
const AuditLog = lazy(() => import('./pages/system/AuditLog'))
const SysConfig = lazy(() => import('./pages/system/SysConfig'))

function PageLoader() {
  return (
    <div className="page-loader">
      <Spin size={36} tip="正在加载页面…" />
    </div>
  )
}

function Authenticated({ children }: { children: JSX.Element }) {
  const token = useAuth((state) => state.token)
  const loc = useLocation()
  return token ? children : <Navigate to="/login" state={{ from: `${loc.pathname}${loc.search}` }} replace />
}

function Guard({ children, menu, permission }: { children: JSX.Element; menu?: string; permission?: string }) {
  const { token, mustChangePassword, menus, permissions } = useAuth()
  const loc = useLocation()
  if (!token) return <Navigate to="/login" state={{ from: `${loc.pathname}${loc.search}` }} replace />
  if (mustChangePassword) return <Navigate to="/change-password" replace />
  if (menu && !menus.includes(menu)) return <Navigate to="/" replace />
  if (permission && !permissions.includes(permission)) {
    return <Result status="403" title="无操作权限" subTitle="当前账号仅有此菜单权限，请联系管理员分配相应操作权限。" />
  }
  return children
}

export default function App() {
  const booted = useAuth((s) => s.booted)

  // 启动引导：内存中的 token 随刷新丢失，用 refresh cookie 静默换新
  useEffect(() => {
    bootAuth()
  }, [])

  // 多标签页同步：其他标签登出 → 本地登出；其他标签切换账号 → 跟随刷新
  useEffect(() => {
    const onStorage = (e: StorageEvent) => {
      if (e.key !== 'yf-auth') return
      try {
        const nextState = e.newValue ? JSON.parse(e.newValue)?.state ?? null : null
        const nextUser = nextState?.user ?? null
        const cur = useAuth.getState()
        if (!nextUser && cur.user) {
          cur.logout()
          if (location.pathname !== '/login') location.href = '/login'
        } else if (nextUser && cur.user && nextUser.id !== cur.user.id) {
          location.reload()
        } else if (
          nextUser && cur.user &&
          (JSON.stringify(nextState?.permissions ?? []) !== JSON.stringify(cur.permissions) ||
            JSON.stringify(nextState?.menus ?? []) !== JSON.stringify(cur.menus))
        ) {
          location.reload()
        }
      } catch {
        /* 存储内容异常时忽略 */
      }
    }
    window.addEventListener('storage', onStorage)
    return () => window.removeEventListener('storage', onStorage)
  }, [])

  if (!booted) {
    return (
      <div className="app-boot">
        <img src="/saa-logo.svg" alt="SAA" />
        <Spin size={40} tip="正在恢复登录状态…" />
      </div>
    )
  }

  return (
    <Suspense fallback={<PageLoader />}>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="/change-password" element={<Authenticated><ChangePassword /></Authenticated>} />
        <Route
          path="/"
          element={
            <Guard>
              <AdminLayout />
            </Guard>
          }
        >
          <Route index element={<Dashboard />} />
          <Route path="projects" element={<Guard menu="project:list"><ProjectList /></Guard>} />
          <Route path="projects/:id" element={<Guard menu="project:list"><ProjectDetail /></Guard>} />
          <Route path="suppliers" element={<Guard menu="supplier:list" permission="supplier:manage"><SupplierList /></Guard>} />
          <Route path="org/users" element={<Guard menu="org:user" permission="user:manage"><UserList /></Guard>} />
          <Route path="org/depts" element={<Guard menu="org:dept"><DeptManage /></Guard>} />
          <Route path="rbac/roles" element={<Guard menu="rbac:role" permission="role:manage"><RoleList /></Guard>} />
          <Route path="logs" element={<Guard menu="log:audit" permission="log:view"><AuditLog /></Guard>} />
          <Route path="system/config" element={<Guard menu="system:config" permission="config:manage"><SysConfig /></Guard>} />
          <Route path="*" element={<NotFound />} />
        </Route>
        <Route path="*" element={<NotFound />} />
      </Routes>
    </Suspense>
  )
}
