import { Navigate } from 'react-router-dom'
import WorkOrderLayout, { WorkOrderUnavailable } from '../layouts/WorkOrderLayout'
import { workOrderHome } from '../layouts/navigation'
import { useAuth } from '../store/auth'
import { outsourceHome } from './outsourceNavigation'

export default function OutsourceLayout() {
  return <WorkOrderLayout />
}

export function OutsourceLanding() {
  const home = useAuth(state => outsourceHome(state.permissions) ?? workOrderHome(state))
  return home ? <Navigate to={home} replace /> : <WorkOrderUnavailable />
}
