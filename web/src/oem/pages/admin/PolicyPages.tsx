import { useCallback, useRef, useState } from 'react'
import { Alert, Button, Card, DatePicker, Form, Input, InputNumber, Message, Select, Space, Spin, Switch, Typography } from '@arco-design/web-react'
import { IconSearch } from '@arco-design/web-react/icon'
import { isVersionConflict } from '../../../api/versionConflict'
import { oemRequestError } from '../../api/requestError'
import type { RetentionTemplate, SettingItem } from '../../api/types'
import { useOem } from '../../OemContext'
import { HubToolbar } from '../hubSwitcher'
import { useLatestRequest, useLoadEffect } from '../../useLoadEffect'
import { AuditDetailDrawer, AuditLoadError, AuditResultBar, AuditTable } from '../../../components/AuditLogParts'
import type { AuditLogRow } from '../../../pages/system/auditLogDetails'
import { OEM_ACTION_GROUPS, OEM_AUDIT_PRESENTER, toAuditLogRow } from './oemAuditDetails'

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
    <Card title={<h1 className="visually-hidden">统一删除策略</h1>}>
      <HubToolbar />
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
          <Button type="primary" style={{ alignSelf: 'flex-start' }} loading={saving} disabled={loading || (!current && Boolean(error))} onClick={() => void save()}>保存统一策略</Button>
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

export function SettingsForm({ group }: { group: 'file' | 'notify' }) {
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

interface AuditFilters {
  keyword: string
  action?: string
  range: string[]
}

const EMPTY_AUDIT_FILTERS: AuditFilters = { keyword: '', range: [] }

export function AuditPage() {
  const { api } = useOem()
  const [draft, setDraft] = useState<AuditFilters>(EMPTY_AUDIT_FILTERS)
  const [filters, setFilters] = useState<AuditFilters>(EMPTY_AUDIT_FILTERS)
  const [rows, setRows] = useState<AuditLogRow[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [selected, setSelected] = useState<AuditLogRow | null>(null)
  const beginRequest = useLatestRequest()
  const load = useCallback(async () => {
    const isLatest = beginRequest()
    setLoading(true)
    setLoadError(false)
    try {
      const result = await api.auditLogs({
        page,
        pageSize,
        keyword: filters.keyword || undefined,
        action: filters.action,
        start: filters.range[0] ? new Date(filters.range[0].replace(' ', 'T')).toISOString() : undefined,
        end: filters.range[1] ? new Date(filters.range[1].replace(' ', 'T')).toISOString() : undefined,
      })
      if (!isLatest()) return
      setRows(result.list.map(toAuditLogRow))
      setTotal(result.total)
    } catch {
      if (isLatest()) setLoadError(true)
    } finally {
      if (isLatest()) setLoading(false)
    }
  }, [api, page, pageSize, filters, beginRequest])
  useLoadEffect(load)

  const applyFilters = () => { setPage(1); setFilters({ ...draft }) }
  const resetFilters = () => { setDraft(EMPTY_AUDIT_FILTERS); setFilters(EMPTY_AUDIT_FILTERS); setPage(1) }

  return (
    <Card className="page-card page-card--table audit-page" title={<h1 className="visually-hidden">审计日志</h1>}>
      <div className="audit-filter-panel audit-filter-panel--compact">
        <Input allowClear value={draft.keyword} placeholder="搜索账号或详情" prefix={<IconSearch />} onChange={(value) => setDraft((state) => ({ ...state, keyword: value }))} onPressEnter={applyFilters} />
        <Select allowClear showSearch value={draft.action} placeholder="具体操作" onChange={(value) => setDraft((state) => ({ ...state, action: value as string | undefined }))}>
          {OEM_ACTION_GROUPS.map((group) => (
            <Select.OptGroup key={group.code} label={group.label}>
              {group.options.map(([value, meta]) => <Select.Option key={value} value={value}>{meta.label}</Select.Option>)}
            </Select.OptGroup>
          ))}
        </Select>
        <DatePicker.RangePicker showTime value={draft.range} onChange={(value) => setDraft((state) => ({ ...state, range: (value as string[]) || [] }))} />
        <Space><Button type="primary" onClick={applyFilters}>查询</Button><Button onClick={resetFilters}>重置</Button></Space>
      </div>

      <AuditResultBar total={total} onRefresh={() => void load()} />

      {loadError ? <AuditLoadError onRetry={() => void load()} /> : (
        <AuditTable
          presenter={OEM_AUDIT_PRESENTER}
          rows={rows}
          loading={loading}
          total={total}
          page={page}
          pageSize={pageSize}
          onPageChange={(nextPage, nextSize) => { setPage(nextPage); setPageSize(nextSize) }}
          onView={setSelected}
        />
      )}

      <AuditDetailDrawer title="审计日志详情" row={selected} presenter={OEM_AUDIT_PRESENTER} onClose={() => setSelected(null)} />
    </Card>
  )
}
