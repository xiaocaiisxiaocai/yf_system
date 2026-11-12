import { lazy } from 'react'
import { Button } from '@arco-design/web-react'
import { IconLock } from '@arco-design/web-react/icon'
import { Navigate, useNavigate } from 'react-router-dom'
import { useShallow } from 'zustand/react/shallow'
import { useAuth } from '../store/auth'
import { businessHomes } from '../layouts/navigation'

const Dashboard = lazy(() => import('./Dashboard'))

export default function Home() {
  const grants = useAuth(useShallow(state => ({ menus: state.menus, permissions: state.permissions, user: state.user })))
  const homes = businessHomes(grants)
  const home = homes.collaboration ?? homes.oem ?? homes.outsource
  const navigate = useNavigate()
  if (home === '/') return <Dashboard />
  if (home) return <Navigate to={home} replace />
  return (
    <div className="access-empty">
      <section className="access-empty-card" aria-labelledby="access-empty-title">
        <span className="access-empty-icon" aria-hidden="true"><IconLock /></span>
        <h1 id="access-empty-title">暂未开通功能权限</h1>
        <p>请联系管理员分配所需权限。</p>
        <Button type="primary" onClick={() => navigate('/profile')}>个人资料与密码</Button>
      </section>
    </div>
  )
}
