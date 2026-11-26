import { Navigate } from 'react-router-dom'
import WorkOrderLayout from '../layouts/WorkOrderLayout'
import { OUTSOURCE_HOME } from './outsourceNavigation'

export default function OutsourceLayout() {
  return <WorkOrderLayout />
}

export function OutsourceLanding() {
  return <Navigate to={OUTSOURCE_HOME} replace />
}
