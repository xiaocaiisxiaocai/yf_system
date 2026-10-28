import type { ReactNode } from 'react'
import { Card, Tabs } from '@arco-design/web-react'
import { useSearchParams } from 'react-router-dom'
import { useCan } from '../OemContext'
import ApprovalInboxPage from './ApprovalInboxPage'
import BlockedRecoveryPage from './BlockedRecoveryPage'
import FlowTemplatesPage from './admin/FlowTemplatesPage'
import { RetentionPage, SettingsForm } from './admin/PolicyPages'

interface HubTab { key: string; title: string; visible: boolean; render: () => ReactNode }

/**
 * One menu entry for several related pages. Each tab keeps its own permission and page; the
 * active tab lives in `?tab=` so old addresses can redirect to it.
 */
function TabHub({ tabs }: { tabs: HubTab[] }) {
  const [params, setParams] = useSearchParams()
  const visible = tabs.filter((tab) => tab.visible)
  const active = visible.find((tab) => tab.key === params.get('tab')) ?? visible[0]
  if (!active) return null
  return (
    <>
      {visible.length > 1 && (
        <Tabs className="oem-hub-tabs" activeTab={active.key} onChange={(key) => setParams({ tab: key }, { replace: true })}>
          {visible.map((tab) => <Tabs.TabPane key={tab.key} title={tab.title} />)}
        </Tabs>
      )}
      {active.render()}
    </>
  )
}

export function ApprovalsHub() {
  const can = useCan()
  return <TabHub tabs={[
    { key: 'inbox', title: '待我审批', visible: can.approve, render: () => <ApprovalInboxPage /> },
    { key: 'blocked', title: '审批异常', visible: can.recover, render: () => <BlockedRecoveryPage /> },
  ]} />
}

export function SettingsHub() {
  const can = useCan()
  return <TabHub tabs={[
    { key: 'flow', title: '审批模板', visible: can.manageFlows, render: () => <FlowTemplatesPage /> },
    { key: 'retention', title: '删除策略', visible: can.manageRetention, render: () => <RetentionPage /> },
    { key: 'file', title: '文件与下载', visible: can.manageFilePolicy, render: () => <Card key="file" title={<h1 className="visually-hidden">文件与下载策略</h1>}><SettingsForm group="file" /></Card> },
    { key: 'notify', title: '邮件提醒', visible: can.manageNotify, render: () => <Card key="notify" title={<h1 className="visually-hidden">邮件提醒</h1>}><SettingsForm group="notify" /></Card> },
  ]} />
}
