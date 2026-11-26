import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Card, Checkbox, Descriptions, Empty, Message, Select, Space, Spin, Switch, Tabs, TimePicker, Tooltip,
} from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconPlus, IconSync } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../../api/client'
import type { OutsourceDefaultCcEntry, OutsourceDefaultCcRequest } from '../../api/generated/api-types'
import { useAuth } from '../../store/auth'
import { OUTSOURCE_PERMISSIONS } from '../outsourceNavigation'
import useOutsourceSaving from '../useOutsourceSaving'
import { fmtTime, type ApiResponses } from '../../api/types'
import { actionSlots } from '../../components/ActionSlots'
import DangerPopconfirm from '../../components/DangerPopconfirm'
import LoadError from '../../components/LoadError'
import MesMirrorPanel from './MesMirrorPanel'
import OutsourceCustomersTab from './OutsourceCustomersTab'
import OutsourceFieldsTab from './OutsourceFieldsTab'
import OutsourceVendorEmailsTab from './OutsourceVendorEmailsTab'
import './OutsourceSettingsPage.css'
import ResizableTable from '../../components/ResizableTable'
import StatusTag from '../../components/StatusTag'

type Vendor = ApiResponses['GET /outsource/settings/vendors'][number]
type CcUser = ApiResponses['GET /outsource/settings/default-cc'][number]
type CcUserOption = ApiResponses['GET /outsource/settings/cc-user-options'][number]
type SyncStatus = ApiResponses['GET /outsource/sync-status']
type TabKey = 'customers' | 'cc' | 'emails' | 'sync' | 'fields'

const DEFAULT_CC_MODULES = [
  { label: '外发邮件', value: 'OUTSOURCE_NOTICE' },
  { label: '转迅得电控', value: 'ELECTRICAL_HANDOFF' },
]

/** Purchasing-maintained reference data of the outsourcing list (外协清单契约 → 设定维护). */
export default function OutsourceSettingsPage() {
  // The route guard requires outsource:settings, which covers every tab here (field settings included).
  const [tab, setTab] = useState<TabKey>('customers')
  const [vendors, setVendors] = useState<Vendor[]>([])
  const [cc, setCc] = useState<CcUser[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const sequence = useRef(0)

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const [vendorResponse, ccResponse] = await Promise.all([
        http.get<ApiResponses['GET /outsource/settings/vendors']>('/outsource/settings/vendors'),
        http.get<ApiResponses['GET /outsource/settings/default-cc']>('/outsource/settings/default-cc'),
      ])
      if (seq === sequence.current) { setVendors(vendorResponse.data); setCc(ccResponse.data) }
    } catch {
      if (seq === sequence.current) setError(true)
    } finally {
      if (seq === sequence.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; sequence.current += 1 }
  }, [load])

  return <Card className="page-card page-card--table outsource-settings-page">
    <h1 className="visually-hidden">外发工令设定</h1>
    <Tabs className="outsource-settings-tabs" activeTab={tab} onChange={(value) => setTab(value as TabKey)}>
      <Tabs.TabPane key="customers" title="客户简称" />
      <Tabs.TabPane key="cc" title="默认抄送" />
      <Tabs.TabPane key="emails" title="外协邮箱" />
      <Tabs.TabPane key="sync" title="IPMS 同步" />
      <Tabs.TabPane key="fields" title="栏位设定" />
    </Tabs>
    {tab === 'customers' ? <OutsourceCustomersTab />
      : tab === 'sync' ? <SyncTab />
      : tab === 'fields' ? <OutsourceFieldsTab />
      : error ? <LoadError message="设定加载失败" onRetry={load} />
      : tab === 'cc' ? <DefaultCcTab cc={cc} loading={loading} onChange={setCc} />
      : <OutsourceVendorEmailsTab vendors={vendors} />}
  </Card>
}

function DefaultCcTab({ cc, loading, onChange }: { cc: CcUser[]; loading: boolean; onChange: (rows: CcUser[]) => void }) {
  const [options, setOptions] = useState<CcUserOption[]>([])
  const [picked, setPicked] = useState<number>()
  const [pickedModules, setPickedModules] = useState(['OUTSOURCE_NOTICE'])
  const { saving, run } = useOutsourceSaving()
  const busy = loading || saving
  const [optionsLoading, setOptionsLoading] = useState(false)
  const optionsLoaded = useRef(false)

  // 一次取回全部启用的内部账号，在本地搜索：不截断，未填邮箱的人显示为不可选。
  const loadOptions = async () => {
    if (optionsLoaded.current) return
    optionsLoaded.current = true
    setOptionsLoading(true)
    try {
      const response = await http.get<ApiResponses['GET /outsource/settings/cc-user-options']>('/outsource/settings/cc-user-options')
      setOptions(response.data)
    } catch { optionsLoaded.current = false /* 统一请求层提示 */ } finally { setOptionsLoading(false) }
  }
  const entries = cc.map(({ userId, modules }): OutsourceDefaultCcEntry => ({ userId, modules }))
  const replace = (next: OutsourceDefaultCcEntry[], message: string, onSaved?: () => void) => run(async () => {
    const response = await http.put<ApiResponses['PUT /outsource/settings/default-cc']>('/outsource/settings/default-cc',
      { entries: next } satisfies OutsourceDefaultCcRequest)
    onChange(response.data); Message.success(message)
    onSaved?.()
  })
  const add = () => {
    if (busy || picked === undefined || pickedModules.length === 0) return
    void replace([...entries, { userId: picked, modules: pickedModules }], '已加入默认抄送', () => {
      setPicked(undefined); setPickedModules(['OUTSOURCE_NOTICE'])
    })
  }

  const columns: TableColumnProps<CcUser>[] = [
    { title: '姓名', dataIndex: 'realName', width: 140, ellipsis: true },
    { title: '账号', dataIndex: 'employeeNo', width: 140, ellipsis: true },
    { title: '邮箱', dataIndex: 'email', width: 260, align: 'left', ellipsis: true },
    {
      title: '适用模块', width: 240, align: 'left',
      render: (_, row) => <div role="group" aria-label={`${row.realName}的适用模块`}>
        <Space>
          {DEFAULT_CC_MODULES.map(({ label, value }) => {
            const checked = row.modules.includes(value)
            return <Checkbox key={value} checked={checked}
              disabled={busy || (checked && row.modules.length === 1)}
              onChange={(enabled) => {
                const modules = enabled ? [...row.modules, value] : row.modules.filter((module) => module !== value)
                if (modules.length === 0) return
                void replace(entries.map((entry) => entry.userId === row.userId ? { ...entry, modules } : entry), '已保存适用模块')
              }}>{label}</Checkbox>
          })}
        </Space>
      </div>,
    },
    { title: '状态', width: 90, align: 'center' as const, render: (_: unknown, row: CcUser) => <StatusTag status={row.status} /> },
    {
      title: '操作', width: 100, align: 'center' as const, render: (_: unknown, row: CcUser) => actionSlots([
        <DangerPopconfirm key="remove" title={`将“${row.realName}”移出默认抄送？`} disabled={busy}
          onOk={() => replace(entries.filter((item) => item.userId !== row.userId), '已移出默认抄送')}>
          <Button type="text" size="mini" status="danger" disabled={busy}>移除</Button>
        </DangerPopconfirm>,
      ], 'single'),
    },
  ]

  return <>
    <div className="page-toolbar responsive-toolbar outsource-settings-toolbar">
      <Space wrap>
        <Select aria-label="选择抄送人员" placeholder="搜索姓名、账号或邮箱" showSearch allowClear style={{ width: 360 }}
          loading={optionsLoading} disabled={busy} value={picked} onChange={setPicked} onFocus={() => void loadOptions()}
          filterOption={(input, option) => String((option as { props?: { children?: unknown } }).props?.children ?? '')
            .toLowerCase().includes(input.trim().toLowerCase())}
          options={options.filter((option) => !cc.some((row) => row.userId === option.userId))
            .map((option) => ({
              label: `${option.realName} · ${option.employeeNo} · ${option.email || '未填写邮箱'}`,
              value: option.userId, disabled: !option.email,
            }))} />
        <div role="group" aria-label="新增人员的适用模块">
          <Checkbox.Group options={DEFAULT_CC_MODULES} value={pickedModules} disabled={busy} onChange={setPickedModules} />
        </div>
        <Button type="primary" icon={<IconPlus />} disabled={busy || picked === undefined || pickedModules.length === 0} onClick={add}>加入</Button>
      </Space>
    </div>
    <ResizableTable<CcUser> tableId="outsource-default-cc" className="page-table" rowKey="userId" loading={loading} data={cc} pagination={false}
      noDataElement={loading ? <span /> : <Empty description="暂无默认抄送人员" />} scroll={{ x: 970, y: 'var(--page-table-scroll-y)' }}
      columns={columns} />
  </>
}

const SYNC_POLL_MS = 3000
const syncTime = (value?: string | null) => fmtTime(value)

function SyncTab() {
  const [status, setStatus] = useState<SyncStatus | null>(null)
  const [time, setTime] = useState<string>()
  const [error, setError] = useState(false)
  const { saving, run } = useOutsourceSaving()
  // Manual sync of the open items is the list page's "立即同步" and needs the same permission.
  const canSync = useAuth((state) => state.permissions.includes(OUTSOURCE_PERMISSIONS.manage))
  const { saving: syncing, run: runSync } = useOutsourceSaving()
  const mounted = useRef(true)
  const refreshInFlight = useRef(false)

  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])

  const refreshStatus = useCallback(async (quiet = false) => {
    if (refreshInFlight.current) return
    refreshInFlight.current = true
    try {
      const response = await http.get<ApiResponses['GET /outsource/sync-status']>(
        '/outsource/sync-status',
        quiet ? ({ quietNetworkError: true } as QuietRequestConfig) : undefined,
      )
      if (!mounted.current) return
      setStatus(response.data)
    } finally {
      refreshInFlight.current = false
    }
  }, [])

  const load = useCallback(async () => {
    setError(false)
    try {
      const response = await http.get<ApiResponses['GET /outsource/sync-status']>('/outsource/sync-status')
      if (!mounted.current) return
      setStatus(response.data); setTime(response.data.syncTime)
    } catch { if (mounted.current) setError(true) }
  }, [])
  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false }
  }, [load])

  const running = Boolean(status?.running)
  useEffect(() => {
    if (!running) return
    let active = true
    let polling = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const isHidden = () => document.hidden || document.visibilityState === 'hidden'
    const clearTimer = () => {
      if (timer !== undefined) {
        clearTimeout(timer)
        timer = undefined
      }
    }
    const schedule = () => {
      clearTimer()
      if (!active || isHidden() || polling) return
      timer = setTimeout(() => {
        timer = undefined
        void tick()
      }, SYNC_POLL_MS)
    }
    const tick = async () => {
      if (!active || isHidden() || polling) return
      polling = true
      try {
        await refreshStatus(true)
      } catch {
        // 后台轮询静默忽略单次失败，等待下一轮重试。
      } finally {
        polling = false
        if (active && !isHidden()) schedule()
      }
    }
    const onVisibility = () => {
      if (isHidden()) {
        clearTimer()
        return
      }
      if (!polling) {
        clearTimer()
        void tick()
      }
    }
    schedule()
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      active = false
      clearTimer()
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [running, refreshStatus])

  const setSchedule = (enabled: boolean) => run(async () => {
    const response = await http.put<ApiResponses['PUT /outsource/settings/sync-schedule']>('/outsource/settings/sync-schedule', { enabled })
    if (!mounted.current) return
    setStatus(response.data); Message.success(enabled ? '定时同步已开启' : '定时同步已停止')
  })

  const save = () => run(async () => {
    if (!time) return
    const response = await http.put<ApiResponses['PUT /outsource/settings/sync-time']>('/outsource/settings/sync-time', { syncTime: time })
    if (!mounted.current) return
    setStatus(response.data); setTime(response.data.syncTime); Message.success('同步时间已保存')
  })

  const syncNow = () => runSync(async () => {
    if (!status?.ipmsConfigured) return
    const response = await http.post<ApiResponses['POST /outsource/items/sync']>('/outsource/items/sync', { itemIds: null })
    if (!mounted.current) return
    setStatus(response.data)
    Message.success(`已同步 ${response.data.lastProcessed} 条，${response.data.lastChanged} 条有变更`)
  })

  if (error) return <LoadError message="同步状态加载失败" onRetry={load} />
  if (!status) return <Spin style={{ display: 'block', padding: '48px 0' }} />
  const trigger = status.lastTrigger === 'SCHEDULED' ? '定时' : status.lastTrigger === 'MANUAL' ? '手动' : '-'
  return <div className="outsource-sync-tab">
    <MesMirrorPanel />
    <section className="outsource-sync-section">
    <h2 className="outsource-sync-section__title">更新外发工令</h2>
    <Space wrap>
      <span>每日同步时间</span>
      <TimePicker aria-label="每日同步时间" format="HH:mm" value={time} allowClear={false} onChange={(value) => setTime(value)} style={{ width: 120 }} />
      <Button type="primary" loading={saving} disabled={!time || time === status.syncTime} onClick={() => void save()}>保存</Button>
      <span>定时同步</span>
      <Switch aria-label="定时同步" checkedText="开启" uncheckedText="停止" checked={status.scheduleEnabled} disabled={saving}
        onChange={(value) => void setSchedule(value)} />
      {canSync && (
        <Tooltip content="IPMS 未配置，暂不可同步" disabled={Boolean(status.ipmsConfigured)}>
          <Button icon={<IconSync />} loading={syncing || status.running} disabled={!status.ipmsConfigured}
            onClick={() => void syncNow()}>立即同步</Button>
        </Tooltip>
      )}
    </Space>
    <Descriptions column={1} border className="outsource-sync-descriptions" data={[
      { label: '最近成功', value: syncTime(status.lastSuccessAt) },
      { label: '最近一次', value: `${syncTime(status.lastFinishedAt)}（${trigger}）` },
      { label: '结果', value: status.lastError
        ? <span className="outsource-sync-error">{status.lastError}</span>
        : `处理 ${status.lastProcessed}，变更 ${status.lastChanged}，结案 ${status.lastClosed}，IPMS 查无 ${status.lastMissing}` },
    ]} />
    </section>
  </div>
}
