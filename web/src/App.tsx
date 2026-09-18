import { lazy, Suspense, useEffect } from 'react'
import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { Result, Spin } from '@arco-design/web-react'
import { useAuth } from './store/auth'
import { bootAuth } from './api/client'

const AdminLayout = lazy(() => import('./layouts/AdminLayout'))
const Login = lazy(() => import('./pages/Login'))
const ChangePassword = lazy(() => import('./pages/ChangePassword'))
const Profile = lazy(() => import('./pages/Profile'))
const Dashboard = lazy(() => import('./pages/Dashboard'))
const NotFound = lazy(() => import('./pages/NotFound'))
const ProjectList = lazy(() => import('./pages/project/ProjectList'))
const ProjectDetail = lazy(() => import('./pages/project/ProjectDetail'))
const ProjectGroupDetail = lazy(() => import('./pages/project/ProjectGroupDetail'))
const SupplierList = lazy(() => import('./pages/supplier/SupplierList'))
const UserList = lazy(() => import('./pages/org/UserList'))
const DeptManage = lazy(() => import('./pages/org/DeptManage'))
const RoleList = lazy(() => import('./pages/rbac/RoleList'))
const AuditLog = lazy(() => import('./pages/system/AuditLog'))
const SysConfig = lazy(() => import('./pages/system/SysConfig'))
const Dictionaries = lazy(() => import('./pages/system/Dictionaries'))
// OEM business line: separate entries, layouts and (for vendors) a separate session.
const OemInternalLayout = lazy(() => import('./oem/layouts/OemLayouts').then((m) => ({ default: m.OemInternalLayout })))
const PortalLayout = lazy(() => import('./oem/layouts/OemLayouts').then((m) => ({ default: m.PortalLayout })))
const OemTransferList = lazy(() => import('./oem/pages/TransferListPage'))
const OemTransferDetail = lazy(() => import('./oem/pages/TransferDetailPage'))
const OemApprovals = lazy(() => import('./oem/pages/ApprovalInboxPage'))
const OemCompanies = lazy(() => import('./oem/pages/admin/CompaniesPage'))
const OemFlowTemplates = lazy(() => import('./oem/pages/admin/FlowTemplatesPage'))
const OemLeaders = lazy(() => import('./oem/pages/admin/LeadersPage'))
const OemRetention = lazy(() => import('./oem/pages/admin/PolicyPages').then((m) => ({ default: m.RetentionPage })))
const OemSettings = lazy(() => import('./oem/pages/admin/PolicyPages').then((m) => ({ default: m.SettingsPage })))
const OemAudit = lazy(() => import('./oem/pages/admin/PolicyPages').then((m) => ({ default: m.AuditPage })))
const PortalLogin = lazy(() => import('./oem/pages/portal/PortalAuthPages').then((m) => ({ default: m.PortalLoginPage })))
const PortalChangePassword = lazy(() => import('./oem/pages/portal/PortalAuthPages').then((m) => ({ default: m.PortalChangePasswordPage })))

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

export function Guard({ children, menu, permission, anyPermission }: { children: JSX.Element; menu?: string; permission?: string; anyPermission?: string[] }) {
  const { token, mustChangePassword, menus, permissions } = useAuth()
  const loc = useLocation()
  if (!token) return <Navigate to="/login" state={{ from: `${loc.pathname}${loc.search}` }} replace />
  if (mustChangePassword) return <Navigate to="/change-password" replace />
  if (menu && !menus.includes(menu)) return <Navigate to="/" replace />
  if (permission && !permissions.includes(permission)) {
    return <Result status="403" title="无操作权限" subTitle="当前账号仅有此菜单权限，请联系管理员分配相应操作权限。" />
  }
  if (anyPermission && !anyPermission.some((item) => permissions.includes(item))) {
    return <Result status="403" title="无操作权限" subTitle="当前账号仅有此菜单权限，请联系管理员分配相应操作权限。" />
  }
  return children
}

export default function App() {
  const booted = useAuth((s) => s.booted)
  const token = useAuth((s) => s.token)

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
        <Route path="/oem" element={<Guard><OemInternalLayout /></Guard>}>
          <Route index element={<Navigate to="transfers" replace />} />
          <Route path="transfers" element={<OemTransferList />} />
          <Route path="transfers/:id" element={<OemTransferDetail />} />
          <Route path="approvals" element={<Guard permission="oem:flow_approve"><OemApprovals /></Guard>} />
          <Route path="admin/companies" element={<Guard anyPermission={['oem:company_manage', 'oem:account_manage']}><OemCompanies /></Guard>} />
          <Route path="admin/flow-templates" element={<Guard permission="oem:flow_template_manage"><OemFlowTemplates /></Guard>} />
          <Route path="admin/leaders" element={<Guard permission="dept:leader_manage"><OemLeaders /></Guard>} />
          <Route path="admin/retention" element={<Guard permission="oem:retention_template_manage"><OemRetention /></Guard>} />
          <Route path="admin/settings" element={<Guard anyPermission={['oem:file_policy_manage', 'oem:notify_manage']}><OemSettings /></Guard>} />
          <Route path="admin/audit" element={<Guard permission="oem:audit_view"><OemAudit /></Guard>} />
        </Route>
        <Route path="/oem-portal/login" element={<PortalLogin />} />
        <Route path="/oem-portal/change-password" element={<PortalChangePassword />} />
        <Route path="/oem-portal" element={<PortalLayout />}>
          <Route index element={<Navigate to="transfers" replace />} />
          <Route path="transfers" element={<OemTransferList />} />
          <Route path="transfers/:id" element={<OemTransferDetail />} />
        </Route>
        <Route
          path="/"
          element={
            <Guard>
              <AdminLayout />
            </Guard>
          }
        >
          <Route index element={<Dashboard />} />
          <Route path="profile" element={<Profile />} />
          <Route path="projects" element={<Guard menu="project:list"><ProjectList /></Guard>} />
          <Route path="project-groups/:id" element={<Guard menu="project:list"><ProjectGroupDetail /></Guard>} />
          <Route path="projects/:id" element={<Guard menu="project:list"><ProjectDetail /></Guard>} />
          <Route path="suppliers" element={<Guard menu="supplier:list" anyPermission={['supplier:manage', 'supplier:account']}><SupplierList /></Guard>} />
          <Route path="org/users" element={<Guard menu="org:user" permission="user:manage"><UserList /></Guard>} />
          <Route path="org/depts" element={<Guard menu="org:dept"><DeptManage /></Guard>} />
          <Route path="rbac/roles" element={<Guard menu="rbac:role" permission="role:manage"><RoleList /></Guard>} />
          <Route path="logs" element={<Guard menu="log:audit" permission="log:view"><AuditLog /></Guard>} />
          <Route path="system/config" element={<Guard menu="system:config" permission="config:manage"><SysConfig /></Guard>} />
          <Route path="system/dictionaries" element={<Guard menu="system:config" permission="config:manage"><Dictionaries /></Guard>} />
          {token ? <Route path="*" element={<NotFound />} /> : null}
        </Route>
        <Route path="*" element={<NotFound />} />
      </Routes>
    </Suspense>
  )
}
