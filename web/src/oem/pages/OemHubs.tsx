import type { ReactNode } from 'react'
import { Card, Radio } from '@arco-design/web-react'
import { useSearchParams } from 'react-router-dom'
import { useCan } from '../OemContext'
import ApprovalInboxPage from './ApprovalInboxPage'
import BlockedRecoveryPage from './BlockedRecoveryPage'
import FlowTemplatesPage from './admin/FlowTemplatesPage'
import { RetentionPage, SettingsForm } from './admin/PolicyPages'
import { HubToolbar } from './hubSwitcher'
import { HubSwitcherContext } from './hubSwitcherContext'

interface HubTab { key: string; title: string; visible: boolean; render: () => ReactNode }

/**
 * One menu entry for several related pages. Each section keeps its own permission and page; the
 * switch sits first in that page's toolbar (same control as the transfer direction filter) and the
 * active section lives in `?tab=` so old addresses can redirect to it.
 */
function TabHub({ tabs }: { tabs: HubTab[] }) {
  const [params, setParams] = useSearchParams()
  const visible = tabs.filter((tab) => tab.visible)
  const active = visible.find((tab) => tab.key === params.get('tab')) ?? visible[0]
  if (!active) return null
  const switcher = visible.length > 1 && (
    <Radio.Group type="button" value={active.key} onChange={(key: string) => setParams({ tab: key }, { replace: true })}>
      {visible.map((tab) => <Radio key={tab.key} value={tab.key}>{tab.title}</Radio>)}
    </Radio.Group>
  )
  return <HubSwitcherContext.Provider value={switcher || null}>{active.render()}</HubSwitcherContext.Provider>
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
    { key: 'file', title: '文件与下载', visible: can.manageFilePolicy, render: () => <Card key="file" title={<h1 className="visually-hidden">文件与下载策略</h1>}><HubToolbar /><SettingsForm group="file" /></Card> },
    { key: 'notify', title: '邮件提醒', visible: can.manageNotify, render: () => <Card key="notify" title={<h1 className="visually-hidden">邮件提醒</h1>}><HubToolbar /><SettingsForm group="notify" /></Card> },
  ]} />
}
