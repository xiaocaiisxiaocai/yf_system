import { useCallback, useRef, useState } from 'react'
import { Alert, Button, Card, Form, Input, InputNumber, Message, Select, Space, Spin, Switch, Table, Tabs, Typography } from '@arco-design/web-react'
import { fmtTime } from '../../../api/types'
import { isVersionConflict } from '../../../api/versionConflict'
import { oemRequestError } from '../../api/requestError'
import type { AuditRow, RetentionTemplate, SettingItem } from '../../api/types'
import { useCan, useOem } from '../../OemContext'
import { useLatestRequest, useLoadEffect } from '../../useLoadEffect'
import { ColumnResizeProvider, ColumnVisibilityPicker } from '../../../components/columnResize'
import { type ColumnLayoutSpec } from '../../../components/tableColumnLayout'
import { useAdjustableColumns } from '../../../components/useAdjustableColumns'

const OEM_AUDIT_COLUMNS: readonly ColumnLayoutSpec[] = [
  { key: 'createdAt', title: '时间', defaultWidth: 170, minWidth: 140, locked: true },
  { key: 'action', title: '动作', defaultWidth: 230, minWidth: 120 },
  { key: 'actor', title: '操作人', defaultWidth: 180, minWidth: 110 },
  { key: 'target', title: '对象', defaultWidth: 160, minWidth: 100 },
  { key: 'detail', title: '详情', defaultWidth: 280, minWidth: 160 },
]

const MODES = [
  { value: 'KEEP', label: '不自动删除' },
  { value: 'AFTER_RELEASE', label: '发布后到期删除' },
  { value: 'AFTER_FIRST_RECEIPT', label: '首次接收后宽限删除' },
  { value: 'FIRST_RECEIPT_OR_DEADLINE', label: '首次接收宽限或最晚期限（先到为准）' },
]

export function RetentionPage() {
  const { api } = useOem()
  const [current, setCurrent] = useState<RetentionTemplate | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const savingRef = useRef(false)
  const beginRequest = useLatestRequest()
  const [form] = Form.useForm()
  const mode = Form.useWatch('mode', form) as RetentionTemplate['mode'] | undefined

  const load = useCallback(async () => {
    const isLatest = beginRequest()
    setLoading(true)
    setError('')
    try {
      const rows = await api.retentionTemplates()
      if (!isLatest()) return
      const active = rows.filter((row) => row.status === 'ACTIVE').sort((a, b) => a.id - b.id)[0] ?? null
      setCurrent(active)
      form.resetFields()
      form.setFieldsValue(active ?? { mode: 'KEEP' })
    } catch (cause) {
      if (isLatest()) setError(oemRequestError(cause, '删除策略加载失败，请重试'))
    } finally {
      if (isLatest()) setLoading(false)
    }
  }, [api, form, beginRequest])
  useLoadEffect(load)

  const save = async () => {
    if (savingRef.current) return
    savingRef.current = true
    setSaving(true)
    try {
      const values = await form.validate()
      const body = {
        name: current?.name ?? '统一删除策略', mode: values.mode,
        releaseTtlMinutes: ['AFTER_RELEASE', 'FIRST_RECEIPT_OR_DEADLINE'].includes(values.mode) ? values.releaseTtlMinutes : null,
        receiptGraceMinutes: ['AFTER_FIRST_RECEIPT', 'FIRST_RECEIPT_OR_DEADLINE'].includes(values.mode) ? values.receiptGraceMinutes : null,
      }
      const saved = current
        ? await api.updateRetention(current.id, { ...body, status: 'ACTIVE', version: current.version })
        : await api.createRetention(body)
      setCurrent(saved)
      form.setFieldsValue(saved)
      setError('')
      Message.success('统一策略已保存，仅影响之后提交的文件')
    } catch (cause) {
      // Form validation already marks the invalid field; API failures must keep the user's edits.
      // 40903 只更新版本号。load() 会重置表单，不能用来刷新这次冲突。
      if (isVersionConflict(cause) && current) {
        const rows = await api.retentionTemplates().catch(() => null)
        const active = rows?.filter((row) => row.status === 'ACTIVE').sort((a, b) => a.id - b.id)[0]
        if (active && active.id === current.id) setCurrent((previous) => previous ? { ...previous, version: active.version } : previous)
      }
      const response = (cause as { response?: unknown })?.response
      if (response || cause instanceof Error) setError(oemRequestError(cause, '保存失败，请重试'))
    } finally {
      savingRef.current = false
      setSaving(false)
    }
  }

  return (
    <Card title="统一删除策略">
      <Typography.Paragraph type="secondary">
        所有新提交的文件自动使用此策略，发送人无需选择。修改不影响已提交的文件。
      </Typography.Paragraph>
      {error && <Alert type="error" content={error} action={<Button size="small" onClick={() => void load()} disabled={saving}>重新加载</Button>} style={{ marginBottom: 16 }} />}
      <Spin loading={loading} style={{ display: 'block' }}>
        <Form form={form} layout="vertical" disabled={loading || saving} style={{ maxWidth: 560 }}>
          <Form.Item field="mode" label="删除方式" rules={[{ required: true }]}><Select options={MODES} /></Form.Item>
          {(mode === 'AFTER_RELEASE' || mode === 'FIRST_RECEIPT_OR_DEADLINE') && (
            <Form.Item field="releaseTtlMinutes" label="发布后最长保留（分钟）" rules={[{ required: true }]}>
              <InputNumber min={1} max={5256000} />
            </Form.Item>
          )}
          {(mode === 'AFTER_FIRST_RECEIPT' || mode === 'FIRST_RECEIPT_OR_DEADLINE') && (
            <Form.Item field="receiptGraceMinutes" label="首次完整下载后宽限（分钟）" rules={[{ required: true }]}>
              <InputNumber min={1} max={5256000} />
            </Form.Item>
          )}
          {mode === 'AFTER_FIRST_RECEIPT' && <Typography.Text type="warning">注意：无人下载的文件在此策略下不会自动删除。</Typography.Text>}
          {current && <Typography.Paragraph type="secondary">当前规则：{current.summary}</Typography.Paragraph>}
          <Button type="primary" loading={saving} disabled={loading || (!current && Boolean(error))} onClick={() => void save()}>保存统一策略</Button>
        </Form>
      </Spin>
    </Card>
  )
}

// 以字节为单位的设置：输入框旁换算成 MB/GB，避免管理员数位数。
const BYTE_SETTING_KEYS = new Set([
  'oem.upload.max_file_size',
  'oem.upload.max_transfer_size',
  'oem.upload.chunk_size',
  'oem.upload.max_storage_per_company',
  'oem.validation.archive_max_expanded_bytes',
])

function formatBytes(bytes: number) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${Number.isInteger(value) ? value : value.toFixed(2).replace(/\.?0+$/, '')} ${units[unit]}`
}

function SettingsForm({ group }: { group: 'file' | 'notify' }) {
  const { api } = useOem()
  const [items, setItems] = useState<SettingItem[]>([])
  const [values, setValues] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const beginRequest = useLatestRequest()
  const load = useCallback(async () => {
    const isLatest = beginRequest()
    setLoading(true)
    setError('')
    try {
      const list = await api.settings(group)
      if (!isLatest()) return
      // Do not let a stale server response revive the retired antivirus settings.
      const visible = group === 'file' ? list.filter((item) => !item.key.startsWith('oem.scan.')) : list
      setItems(visible)
      setValues(Object.fromEntries(visible.map((item) => [item.key, item.value])))
    } catch {
      if (isLatest()) setError('策略加载失败，请重试')
    } finally {
      if (isLatest()) setLoading(false)
    }
  }, [api, group, beginRequest])
  useLoadEffect(load)

  const save = async () => {
    const changed = items.filter((item) => !item.readOnly && values[item.key] !== item.value).map((item) => ({ key: item.key, value: values[item.key] }))
    if (changed.length === 0) return
    setSaving(true)
    try {
      await api.updateSettings(group, changed)
      Message.success('已保存')
      await load()
    } catch {
      // The HTTP layer already reported the failure; keep the user's edits for a retry.
    } finally {
      setSaving(false)
    }
  }
  const changed = items.some((item) => !item.readOnly && values[item.key] !== item.value)

  return (
    <Space direction="vertical" style={{ width: '100%' }}>
      {group === 'file' && (
        <Typography.Paragraph type="secondary" style={{ margin: '0 0 8px' }}>
          按需设置文件类型、上传容量、压缩包结构及下载限制。
        </Typography.Paragraph>
      )}
      {error && <Alert type="error" content={error} action={<Button size="small" onClick={() => void load()} disabled={saving}>重新加载</Button>} />}
      <Spin loading={loading} style={{ display: 'block' }}>
        {items.map((item) => (
          <Space key={item.key} align="start" wrap style={{ width: '100%', padding: '8px 0' }}>
            <span style={{ display: 'inline-block', width: 260, lineHeight: '32px' }}>{item.label}</span>
            {item.kind === 'boolean'
              ? <Switch aria-label={item.label} disabled={item.readOnly} checked={values[item.key] === 'true'} onChange={(checked) => setValues((v) => ({ ...v, [item.key]: String(checked) }))} />
              : item.kind === 'integer'
                ? <InputNumber style={{ width: 220 }} value={Number(values[item.key])}
                    min={item.min ?? undefined} max={item.max ?? undefined} aria-label={item.label} disabled={item.readOnly}
                    onChange={(value) => setValues((v) => ({ ...v, [item.key]: value === undefined ? '' : String(value) }))} />
              : <Input style={{ width: item.kind === 'extensions' ? 520 : 220 }} value={values[item.key]}
                  aria-label={item.label} disabled={item.readOnly}
                  onChange={(value) => setValues((v) => ({ ...v, [item.key]: value }))} />}
            {BYTE_SETTING_KEYS.has(item.key) && Number(values[item.key]) > 0 && (
              <Typography.Text type="secondary" className="oem-setting-size">约 {formatBytes(Number(values[item.key]))}</Typography.Text>
            )}
            {item.unsupportedReason && <Typography.Text type="warning">{item.unsupportedReason}</Typography.Text>}
            {item.hint && <Typography.Text type="secondary">{item.hint}</Typography.Text>}
          </Space>
        ))}
      </Spin>
      <Button type="primary" disabled={!changed || loading} loading={saving} onClick={() => void save()}>保存更改</Button>
    </Space>
  )
}

export function SettingsPage() {
  const can = useCan()
  return (
    <Card title="文件策略与邮件提醒">
      <Tabs defaultActiveTab={can.manageFilePolicy ? 'file' : 'notify'}>
        {can.manageFilePolicy && <Tabs.TabPane key="file" title="文件与下载策略"><SettingsForm group="file" /></Tabs.TabPane>}
        {can.manageNotify && <Tabs.TabPane key="notify" title="邮件提醒"><SettingsForm group="notify" /></Tabs.TabPane>}
      </Tabs>
    </Card>
  )
}

export function AuditPage() {
  const { api, userId } = useOem()
  const [rows, setRows] = useState<AuditRow[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [keyword, setKeyword] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const tableHostRef = useRef<HTMLDivElement>(null)
  const beginRequest = useLatestRequest()
  const load = useCallback(async () => {
    const isLatest = beginRequest()
    setLoading(true)
    setError('')
    try {
      const result = await api.auditLogs({ page, pageSize: 20, keyword: keyword || undefined })
      if (!isLatest()) return
      setRows(result.list)
      setTotal(result.total)
    } catch {
      if (isLatest()) setError('审计日志加载失败，请重试')
    } finally {
      if (isLatest()) setLoading(false)
    }
  }, [api, page, keyword, beginRequest])
  useLoadEffect(load)
  const auditColumns = [
    { key: 'createdAt', title: '时间', render: (_: unknown, row: AuditRow) => fmtTime(row.createdAt) },
    { key: 'action', title: '动作', dataIndex: 'action' },
    {
      key: 'actor', title: '操作人',
      render: (_: unknown, row: AuditRow) => row.actorRealm === 'system' ? '系统'
        : `${row.employeeNo ?? '-'}${row.actorRealm === 'oem' ? '（OEM）' : ''}`,
    },
    { key: 'target', title: '对象', render: (_: unknown, row: AuditRow) => (row.targetType ? `${row.targetType} #${row.targetId ?? ''}` : '-') },
    {
      key: 'detail', title: '详情',
      render: (_: unknown, row: AuditRow) => {
        const detail = { ...(row.detail ?? {}) }
        delete (detail as Record<string, unknown>).auditContext
        return <Typography.Text ellipsis={{ showTooltip: true }} style={{ maxWidth: 420 }}>{JSON.stringify(detail)}</Typography.Text>
      },
    },
  ]
  const table = useAdjustableColumns('oem-audit', userId, OEM_AUDIT_COLUMNS, auditColumns, 'createdAt', tableHostRef)

  return (
    <Card title="OEM 审计日志">
      <Space style={{ marginBottom: 12 }}>
        <Input.Search allowClear placeholder="搜索账号、动作或内容" style={{ width: 280 }} onSearch={(value) => { setKeyword(value); setPage(1) }} />
        <ColumnVisibilityPicker
          specs={OEM_AUDIT_COLUMNS}
          hidden={table.hidden}
          onToggle={table.toggle}
          onShowAll={table.showAll}
          onHideOptional={table.hideOptional}
          onReset={table.reset}
        />
      </Space>
      {error && <Alert type="error" style={{ marginBottom: 12 }} content={error} action={<Button size="small" onClick={() => void load()}>重新加载</Button>} />}
      <ColumnResizeProvider actions={table.actions}>
        <div ref={tableHostRef} className={table.hostClassName}>
          <Table
            className={table.tableClassName}
            rowKey="id"
            loading={loading}
            data={rows}
            columns={table.columns}
            components={table.components}
            scroll={{ x: table.scrollX }}
            pagination={{ current: page, total, pageSize: 20, onChange: setPage }}
          />
        </div>
      </ColumnResizeProvider>
    </Card>
  )
}
