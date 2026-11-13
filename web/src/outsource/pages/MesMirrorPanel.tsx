import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Descriptions, Input, InputNumber, Message, Modal, Space, Switch, Tag, TimePicker } from '@arco-design/web-react'
import http from '../../api/client'
import { fmtTime, type ApiResponses } from '../../api/types'
import ResizableTable from '../../components/ResizableTable'
import LoadError from '../../components/LoadError'

type Status = ApiResponses['GET /outsource/mes-mirror/status']
type Settings = ApiResponses['GET /outsource/mes-mirror/settings']
type TestResult = ApiResponses['POST /outsource/mes-mirror/test']
type PreviewRow = Status['preview'][number]

const POLL_MS = 3000
/** IPMS answers slowly (about 40 s a query), and the server allows a test up to three minutes. */
const TEST_TIMEOUT_MS = 200_000
const BUSY = new Set(['RUNNING', 'STOPPING'])
const validSize = (value: number | undefined) => Number.isInteger(value) && (value as number) >= 1 && (value as number) <= 200
const validPage = (value: number | undefined) => Number.isInteger(value) && (value as number) >= 1

const PREVIEW_COLUMNS = [
  { title: '工令', dataIndex: 'workOrderNo', width: 130 },
  { title: '客户', dataIndex: 'customerName', width: 240, ellipsis: true },
  { title: '机型', dataIndex: 'deviceModels', width: 160, ellipsis: true },
]

/** IPMS (its MES monitoring module) connection, daily fetch time and manual fetches, on the outsourcing settings page. */
export default function MesMirrorPanel() {
  const [status, setStatus] = useState<Status | null>(null)
  const [settings, setSettings] = useState<Settings | null>(null)
  const [address, setAddress] = useState('')
  const [account, setAccount] = useState('')
  const [password, setPassword] = useState('')
  const [fetchTime, setFetchTime] = useState<string>()
  const [pageSize, setPageSize] = useState<number | undefined>()
  const [page, setPage] = useState<number | undefined>(1)
  const [testResult, setTestResult] = useState<TestResult | null>(null)
  const [error, setError] = useState(false)
  const [working, setWorking] = useState(false)
  const [testing, setTesting] = useState(false)
  const lock = useRef(false)

  const refresh = useCallback(async () => {
    const response = await http.get<ApiResponses['GET /outsource/mes-mirror/status']>('/outsource/mes-mirror/status')
    setStatus(response.data)
    setPageSize((current) => current ?? response.data.pageSize)
  }, [])

  const applySettings = (next: Settings) => {
    setSettings(next); setAddress(next.baseUrl); setAccount(next.account); setPassword(''); setFetchTime(next.syncTime)
  }

  const load = useCallback(async () => {
    setError(false)
    try {
      const [statusResponse, settingsResponse] = await Promise.all([
        http.get<ApiResponses['GET /outsource/mes-mirror/status']>('/outsource/mes-mirror/status'),
        http.get<ApiResponses['GET /outsource/mes-mirror/settings']>('/outsource/mes-mirror/settings'),
      ])
      setStatus(statusResponse.data)
      setPageSize((current) => current ?? statusResponse.data.pageSize)
      applySettings(settingsResponse.data)
    } catch { setError(true) }
  }, [])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false }
  }, [load])

  const phase = status?.phase ?? 'IDLE'
  const busy = BUSY.has(phase)
  useEffect(() => {
    if (!busy) return
    const timer = setInterval(() => { void refresh().catch(() => undefined) }, POLL_MS)
    return () => clearInterval(timer)
  }, [busy, refresh])

  /** One action at a time; the request layer shows errors, so the form keeps what was typed. */
  const run = async (action: () => Promise<void>) => {
    if (lock.current) return
    lock.current = true; setWorking(true)
    try { await action() } catch { /* 统一请求层提示 */ } finally { lock.current = false; setWorking(false) }
  }

  const start = (body: { mode: 'ALL' } | { mode: 'PAGE'; page: number }) => run(async () => {
    const response = await http.post<ApiResponses['POST /outsource/mes-mirror/run']>('/outsource/mes-mirror/run', { ...body, pageSize })
    setStatus(response.data)
  })
  const stop = () => run(async () => {
    const response = await http.post<ApiResponses['POST /outsource/mes-mirror/stop']>('/outsource/mes-mirror/stop')
    setStatus(response.data)
  })
  const confirm = (apply: boolean) => run(async () => {
    const response = await http.post<ApiResponses['POST /outsource/mes-mirror/confirm']>('/outsource/mes-mirror/confirm', { apply })
    setStatus(response.data)
    Message.success(apply ? '已合并' : '已放弃，资料未改动')
  })
  const setSchedule = (enabled: boolean) => run(async () => {
    const response = await http.put<ApiResponses['PUT /outsource/mes-mirror/schedule']>('/outsource/mes-mirror/schedule', { enabled })
    setSettings(response.data)
    Message.success(enabled ? '定时抓取已开启' : '定时抓取已停止')
  })
  const save = () => run(async () => {
    const response = await http.put<ApiResponses['PUT /outsource/mes-mirror/settings']>('/outsource/mes-mirror/settings',
      { baseUrl: address.trim(), account: account.trim(), password: password || null, syncTime: fetchTime })
    applySettings(response.data); setTestResult(null)
    Message.success('IPMS 设定已保存')
  })
  const test = () => run(async () => {
    setTestResult(null); setTesting(true)
    try {
      const response = await http.post<ApiResponses['POST /outsource/mes-mirror/test']>('/outsource/mes-mirror/test',
        { baseUrl: address.trim(), account: account.trim(), password: password || null }, { timeout: TEST_TIMEOUT_MS })
      setTestResult(response.data)
    } finally { setTesting(false) }
  })

  if (error) return <LoadError message="IPMS 设定加载失败" onRetry={load} />
  if (!status || !settings) return null

  const canStart = !busy && phase !== 'AWAITING_CONFIRM' && !working
  const trigger = status.lastTrigger === 'SCHEDULED' ? '定时' : status.lastTrigger === 'MANUAL' ? '手动' : '—'
  const dirty = address.trim() !== settings.baseUrl || account.trim() !== settings.account || password !== '' || fetchTime !== settings.syncTime
  const pending = phase === 'AWAITING_CONFIRM'
  const pendingText = status.pendingReason === 'PAGE_FETCHED' ? `第 ${status.page} 页` : '已停止'

  return <section className="outsource-sync-section">
    <div className="outsource-sync-section__head">
      <h2 className="outsource-sync-section__title">IPMS 连接</h2>
      {settings.accountConfigured ? <Tag color="green">已设定</Tag> : <Tag color="gray">未设定</Tag>}
    </div>
    <div className="ipms-connection-form">
      <label htmlFor="ipms-address">地址</label>
      <Input id="ipms-address" aria-label="IPMS 地址" value={address} onChange={setAddress} allowClear
        placeholder={settings.configBaseUrl ?? 'http://主机:端口'} disabled={!canStart} />
      <label htmlFor="ipms-account">账号</label>
      <Input id="ipms-account" aria-label="IPMS 账号" value={account} onChange={setAccount} allowClear autoComplete="off" disabled={!canStart} />
      <label htmlFor="ipms-password">密码</label>
      <Input.Password id="ipms-password" aria-label="IPMS 密码" value={password} onChange={setPassword} autoComplete="new-password"
        placeholder={settings.passwordSet ? '已设定，留空不修改' : '请输入密码'} disabled={!canStart} />
      <span>每日抓取时间</span>
      <TimePicker aria-label="每日抓取时间" format="HH:mm" value={fetchTime} allowClear={false}
        onChange={(value) => setFetchTime(value)} style={{ width: 120 }} disabled={!canStart} />
      <span>定时抓取</span>
      <Switch aria-label="定时抓取" checkedText="开启" uncheckedText="停止" checked={settings.scheduleEnabled} disabled={working}
        onChange={(value) => void setSchedule(value)} />
    </div>
    <Space wrap>
      <Button type="primary" disabled={!canStart || !dirty} onClick={() => void save()}>保存</Button>
      <Button loading={testing} disabled={!canStart} onClick={() => void test()}>测试连接</Button>
    </Space>
    {testResult && <div role="status" className={testResult.ok ? 'mes-mirror-panel__ok' : 'outsource-sync-error'}>
      {testResult.message}（{(testResult.elapsedMs / 1000).toFixed(1)} 秒）
    </div>}
    <h2 className="outsource-sync-section__title">抓取资料</h2>
    <Space wrap>
      <span>每页条数</span>
      <InputNumber aria-label="每页条数" style={{ width: 110 }} value={pageSize} precision={0} hideControl
        error={!validSize(pageSize)} onChange={(value) => setPageSize(value as number | undefined)} />
      <Button type="primary" disabled={!canStart || !validSize(pageSize)} onClick={() => void start({ mode: 'ALL' })}>抓取全部</Button>
      <span>页码</span>
      <InputNumber aria-label="页码" style={{ width: 100 }} value={page} precision={0} hideControl
        error={!validPage(page)} onChange={(value) => setPage(value as number | undefined)} />
      <Button disabled={!canStart || !validSize(pageSize) || !validPage(page)}
        onClick={() => void start({ mode: 'PAGE', page: page as number })}>抓取指定页</Button>
      {busy && <Button status="danger" disabled={phase === 'STOPPING' || working} onClick={() => void stop()}>停止</Button>}
    </Space>
    {busy && <div role="status">已抓取 {status.fetched} / {status.total ?? '—'} 笔，第 {status.pages} 页{phase === 'STOPPING' ? '，正在停止' : ''}</div>}
    <Descriptions column={1} border className="outsource-sync-descriptions" data={[
      { label: '资料笔数', value: String(status.mirrorCount) },
      { label: '最近成功', value: fmtTime(status.lastSuccessAt) },
      { label: '最近一次', value: `${fmtTime(status.lastFinishedAt)}（${trigger}）` },
      { label: '结果', value: status.lastError ? <span className="outsource-sync-error">{status.lastError}</span> : `共 ${status.lastCount} 笔` },
    ]} />
    <Modal title="确认抓取结果" visible={pending} closable={false} maskClosable={false} escToExit={false} unmountOnExit
      footer={<Space>
        <Button disabled={working} onClick={() => void confirm(false)}>放弃，保留现有数据</Button>
        <Button type="primary" disabled={working} onClick={() => void confirm(true)}>合并已抓部分</Button>
      </Space>}>
      <p>已抓取 {status.fetched} 笔（{pendingText}）。合并会更新或新增这些工令，不会删除已有的其他工令。</p>
      <ResizableTable<PreviewRow> tableId="outsource-mes-preview" size="small" rowKey="workOrderNo" data={status.preview}
        pagination={false} columns={PREVIEW_COLUMNS} scroll={{ x: 530, y: 260 }} />
    </Modal>
  </section>
}
