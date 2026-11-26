import { Card, Tabs } from '@arco-design/web-react'
import { Navigate, useSearchParams } from 'react-router-dom'
import { OUTSOURCE_PERMISSIONS } from '../../../outsource/outsourceNavigation'
import OutsourceTypesTab from '../../../outsource/pages/OutsourceTypesTab'
import { useAuth } from '../../../store/auth'
import { OEM_DIRECTORY_PERMISSIONS } from '../../oemNavigation'
import CompaniesPage from './CompaniesPage'

export default function OutsourceManagementPage() {
  const permissions = useAuth((state) => state.permissions)
  const [params, setParams] = useSearchParams()
  const tabs = [
    {
      key: 'companies', title: '厂商与账号', content: <CompaniesPage />,
      visible: OEM_DIRECTORY_PERMISSIONS.some((code) => permissions.includes(code)),
    },
    {
      key: 'types', title: '外发类型', content: <OutsourceTypesTab />,
      visible: permissions.includes(OUTSOURCE_PERMISSIONS.settings),
    },
  ].filter((tab) => tab.visible)
  const active = tabs.find((tab) => tab.key === params.get('tab')) ?? tabs[0]
  if (!active) return null

  const paramsFor = (key: string) => {
    const next = new URLSearchParams(params)
    next.set('tab', key)
    return next
  }
  if (params.get('tab') !== active.key) {
    return <Navigate to={{ search: paramsFor(active.key).toString() }} replace />
  }

  return (
    <Card className="page-card page-card--table">
      <h1 className="visually-hidden">外协管理设定</h1>
      <Tabs className="outsource-management-tabs" activeTab={active.key}
        onChange={(key) => setParams(paramsFor(key))} destroyOnHide>
        {tabs.map((tab) => <Tabs.TabPane key={tab.key} title={tab.title}>{tab.content}</Tabs.TabPane>)}
      </Tabs>
    </Card>
  )
}
