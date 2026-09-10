import { useCallback, useEffect, useState } from 'react'
import { Button, Card, Input, InputNumber, Message, Progress, Select, Space, Spin, Table, Tag, Typography } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'
import http from '../../api/client'
import { fmtSize, fmtTime } from '../../api/types'

const MB = 1024 * 1024

const CONFIG_META: Record<string, { name: string; description: string; hint?: string }> = {
  'notify.enabled': { name: '邮件通知', description: '邮件通知总开关' },
  'storage.warn_percent': { name: '存储告警阈值', description: '达到该使用率时告警' },
  'upload.allowed_exts': { name: '允许上传类型', description: '允许上传的文件扩展名', hint: '使用英文逗号分隔' },
  'upload.chunk_size': { name: '上传分片大小', description: '每个上传分片的大小' },
  'upload.max_file_size': { name: '单文件大小上限', description: '单个文件允许的最大大小' },
}

interface Cfg {
  key: string
  value: string
  description?: string
  updatedAt?: string
}

interface Storage {
  root: string
  mountPoint?: string
  totalBytes: number
  availableBytes: number
  usedPercent: number
  warnPercent: number
  warning: boolean
}

interface MailDetail {
  eventType?: string
  status?: string
  reason?: string
  employeeNo?: string
  realName?: string
  recipient?: string
  retryCount?: number
  error?: string
}

interface MailRecent {
  id: number
  action: 'EMAIL_SENT' | 'EMAIL_FAILED' | 'EMAIL_RETRY' | 'EMAIL_SKIPPED_MISSING_EMAIL' | string
  targetType?: string | null
  targetId?: string | null
  detail?: MailDetail | null
  createdAt: string
}

interface MailStatus {
  configured: boolean
  host?: string | null
  port?: number | null
  from?: string | null
  notificationsEnabled: boolean
  queue: { pending: number; sending: number; sent: number; failed: number }
  latestSentAt?: string | null
  latestFailedAt?: string | null
  missingEmailCount: number
  missingEmailAccounts: Array<{
    userId: number
    employeeNo: string
    realName: string
    userType: string
    status: string
  }>
  recent: MailRecent[]
}

const EMPTY_MAIL_STATUS: MailStatus = {
  configured: false,
  notificationsEnabled: true,
  queue: { pending: 0, sending: 0, sent: 0, failed: 0 },
  missingEmailCount: 0,
  missingEmailAccounts: [],
  recent: [],
}

function normalizeMailStatus(value: unknown): MailStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return EMPTY_MAIL_STATUS
  const raw = value as Partial<MailStatus>
  return {
    ...EMPTY_MAIL_STATUS,
    ...raw,
    queue: { ...EMPTY_MAIL_STATUS.queue, ...(raw.queue || {}) },
    missingEmailAccounts: Array.isArray(raw.missingEmailAccounts) ? raw.missingEmailAccounts : [],
    recent: Array.isArray(raw.recent) ? raw.recent : [],
  }
}

const MAIL_ACTION_LABELS: Record<string, string> = {
  EMAIL_SENT: '发送成功',
  EMAIL_FAILED: '发送失败',
  EMAIL_RETRY: '发送重试',
  EMAIL_SKIPPED_MISSING_EMAIL: '未入队：缺少邮箱',
}

function mailRecentSummary(row: MailRecent): string {
  const detail = row.detail || {}
  if (row.action === 'EMAIL_SKIPPED_MISSING_EMAIL') {
    return `${detail.realName || '账号'}（${detail.employeeNo || `ID ${row.targetId || '-'}`}）未填写邮箱`
  }
  if (row.action === 'EMAIL_RETRY') {
    return `${detail.error || '发送失败'}${detail.retryCount ? `，已安排第 ${detail.retryCount} 次重试` : ''}`
  }
  if (row.action === 'EMAIL_FAILED') {
    return `${detail.error || '发送失败'}${detail.retryCount ? `，第 ${detail.retryCount} 次` : ''}`
  }
  return detail.recipient ? `收件人 ${detail.recipient}` : '邮件已发送'
}

export default function SysConfig() {
  const navigate = useNavigate()
  const [configs, setConfigs] = useState<Cfg[]>([])
  const [storage, setStorage] = useState<Storage | null>(null)
  const [mail, setMail] = useState<MailStatus | null>(null)
  const [editing, setEditing] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)

  const fetchSnapshot = useCallback(async () => {
    const [configsResponse, storageResponse, mailResponse] = await Promise.all([
      http.get('/admin/system/configs'),
      http.get('/admin/system/storage'),
      http.get('/admin/system/mail-status'),
    ])
    return {
      configs: configsResponse.data as Cfg[],
      storage: storageResponse.data as Storage,
      mail: normalizeMailStatus(mailResponse.data),
    }
  }, [])

  const applySnapshot = useCallback((next: { configs: Cfg[]; storage: Storage; mail: MailStatus }) => {
    setConfigs(next.configs)
    setStorage(next.storage)
    setMail(next.mail || EMPTY_MAIL_STATUS)
    setEditing({})
  }, [])

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    fetchSnapshot().then((next) => {
      if (active) {
        applySnapshot(next)
        setLoadError(false)
      }
    }).catch(() => {
      if (active) setLoadError(true)
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => {
      active = false
    }
  }, [applySnapshot, fetchSnapshot, reloadKey])

  const dirty = Object.entries(editing).filter(([k, v]) => configs.find((c) => c.key === k)?.value !== v)
  const dirtyKeys = new Set(dirty.map(([key]) => key))

  const setValue = (key: string, value: string) => {
    setEditing((current) => ({ ...current, [key]: value }))
  }

  const save = async () => {
    if (dirty.length === 0) return
    setSaving(true)
    try {
      await http.put('/admin/system/configs', { items: dirty.map(([key, value]) => ({ key, value })) })
      Message.success('参数已保存')
      load()
    } catch {
      /* 拦截器已提示 */
    } finally {
      setSaving(false)
    }
  }

  const usedPct = storage
    ? Math.min(100, Math.max(0, Math.round(
      Number.isFinite(storage.usedPercent)
        ? storage.usedPercent
        : storage.totalBytes > 0
          ? ((storage.totalBytes - storage.availableBytes) / storage.totalBytes) * 100
          : 0,
    )))
    : 0

  const editor = (cfg: Cfg) => {
    const value = editing[cfg.key] ?? cfg.value
    const label = CONFIG_META[cfg.key]?.name || cfg.key
    if (cfg.key === 'notify.enabled') {
      return (
        <Select aria-label={label} value={value} onChange={(v) => setValue(cfg.key, String(v))}>
          <Select.Option value="true">启用</Select.Option>
          <Select.Option value="false">关闭</Select.Option>
        </Select>
      )
    }
    if (cfg.key === 'upload.max_file_size' || cfg.key === 'upload.chunk_size') {
      const isChunkSize = cfg.key === 'upload.chunk_size'
      const numericValue = Number(value)
      return (
        <InputNumber
          aria-label={label}
          min={isChunkSize ? 0.25 : 1}
          max={isChunkSize ? 64 : 20 * 1024}
          precision={isChunkSize ? 2 : 0}
          step={isChunkSize ? 0.25 : 1}
          suffix="MB"
          value={Number.isFinite(numericValue) ? numericValue / MB : undefined}
          onChange={(next) => {
            if (typeof next === 'number' && Number.isFinite(next)) {
              setValue(cfg.key, String(Math.round(next * MB)))
            } else {
              setValue(cfg.key, '')
            }
          }}
        />
      )
    }
    if (cfg.key === 'storage.warn_percent') {
      return (
        <InputNumber
          aria-label={label}
          min={1}
          max={99}
          precision={0}
          suffix="%"
          value={Number(value)}
          onChange={(next) => setValue(cfg.key, typeof next === 'number' ? String(next) : '')}
        />
      )
    }
    return <Input aria-label={label} value={value} onChange={(next) => setValue(cfg.key, next)} />
  }

  return (
    <div className="system-page">
      <div className="page-heading"><div><h1>系统参数</h1></div></div>
      {loadError ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={load}>重试</Button>
        </div>
      ) : loading ? (
        <Spin loading style={{ width: '100%', minHeight: 120 }} />
      ) : (
        <>
          {mail && (
            <Card
              className="page-card system-mail-card"
              title="邮件发送"
              extra={
                <Space size={8}>
                  <Tag color={mail.configured ? 'green' : 'red'}>{mail.configured ? 'SMTP 已配置' : 'SMTP 未配置'}</Tag>
                  <Tag color={mail.notificationsEnabled ? 'arcoblue' : 'gray'}>{mail.notificationsEnabled ? '通知已启用' : '通知已关闭'}</Tag>
                </Space>
              }
            >
              <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
                <Space wrap size={20}>
                  <Typography.Text type="secondary">
                    SMTP {mail.configured ? `${mail.host || '-'}:${mail.port || '-'}` : '未配置'}
                  </Typography.Text>
                  {mail.configured && <Typography.Text type="secondary">发件人 {mail.from || '-'}</Typography.Text>}
                  <Typography.Text>待发送 <strong>{mail.queue.pending}</strong></Typography.Text>
                  <Typography.Text>发送中 <strong>{mail.queue.sending}</strong></Typography.Text>
                  <Typography.Text type="success">已发送 <strong>{mail.queue.sent}</strong></Typography.Text>
                  <Typography.Text type={mail.queue.failed > 0 ? 'error' : 'secondary'}>失败 <strong>{mail.queue.failed}</strong></Typography.Text>
                </Space>
                <Space wrap size={20}>
                  <Typography.Text type="secondary">最近成功：{fmtTime(mail.latestSentAt)}</Typography.Text>
                  <Typography.Text type="secondary">最近失败：{fmtTime(mail.latestFailedAt)}</Typography.Text>
                </Space>
                {mail.missingEmailCount > 0 && (
                  <div>
                    <Typography.Text type="warning">
                      {mail.missingEmailCount} 个启用账号未填写邮箱，相关通知不会入队。示例：{mail.missingEmailAccounts.map((account) => `${account.realName}（${account.employeeNo}）`).join('、') || '请查看操作日志'}
                    </Typography.Text>
                    <Button type="text" size="small" onClick={() => navigate('/logs')}>查看操作日志</Button>
                  </div>
                )}
                {mail.recent.length > 0 && (
                  <Table
                    rowKey="id"
                    size="small"
                    pagination={false}
                    data={mail.recent}
                    scroll={{ x: 620 }}
                    columns={[
                      { title: '时间', dataIndex: 'createdAt', width: 160, render: fmtTime },
                      { title: '结果', dataIndex: 'action', width: 150, render: (action: string) => <Tag color={action === 'EMAIL_SENT' ? 'green' : action === 'EMAIL_FAILED' ? 'red' : 'orange'}>{MAIL_ACTION_LABELS[action] || action}</Tag> },
                      { title: '说明', width: 300, render: (_: unknown, row: MailRecent) => mailRecentSummary(row) },
                    ]}
                  />
                )}
              </div>
            </Card>
          )}
          <Card
            className="page-card system-storage-card"
            title="存储状态"
            extra={storage && (
              <Tag color={storage.warning ? 'red' : 'green'}>
                {storage.warning ? '容量告警' : '运行正常'}
              </Tag>
            )}
          >
            {storage && (
              <div className="system-storage-summary">
                <div className="system-storage-progress">
                  <Progress
                    type="circle"
                    width={96}
                    percent={usedPct}
                    color={storage.warning ? '#f53f3f' : '#165dff'}
                  />
                  <span>空间使用率</span>
                </div>
                <div className="system-storage-capacity">
                  <span className="system-storage-label">已用空间</span>
                  <strong>{fmtSize(storage.totalBytes - storage.availableBytes)}</strong>
                  <span>共 {fmtSize(storage.totalBytes)}</span>
                </div>
                <div className="system-storage-metrics">
                  <div className="system-storage-metric system-storage-metric--wide">
                    <span>存储目录</span>
                    <strong title={storage.root}>{storage.root}</strong>
                  </div>
                  <div className="system-storage-metric">
                    <span>可用空间</span>
                    <strong>{fmtSize(storage.availableBytes)}</strong>
                  </div>
                  <div className="system-storage-metric">
                    <span>告警阈值</span>
                    <strong className={storage.warning ? 'is-warning' : ''}>{storage.warnPercent}%</strong>
                  </div>
                  <div className="system-storage-metric system-storage-metric--wide">
                    <span>挂载点</span>
                    <strong title={storage.mountPoint || '-'}>{storage.mountPoint || '-'}</strong>
                  </div>
                </div>
              </div>
            )}
          </Card>
          <Card
            className="page-card system-config-card"
            title="系统参数"
            extra={
              <Space size={8}>
                {dirty.length > 0 && <span className="system-config-dirty">已修改 {dirty.length} 项</span>}
                <Button disabled={dirty.length === 0 || saving} onClick={() => setEditing({})}>重置</Button>
                <Button type="primary" disabled={dirty.length === 0} loading={saving} onClick={save}>保存</Button>
              </Space>
            }
          >
            <Table
              rowKey="key"
              data={configs}
              scroll={{ x: 860 }}
              pagination={false}
              rowClassName={(record) => dirtyKeys.has(record.key) ? 'system-config-row--dirty' : ''}
              columns={[
                {
                  title: '参数',
                  dataIndex: 'key',
                  width: 250,
                  render: (key: string) => (
                    <div className="system-config-param">
                      <strong>{CONFIG_META[key]?.name || key}</strong>
                      <code title={key}>{key}</code>
                    </div>
                  ),
                },
                {
                  title: '设置',
                  dataIndex: 'value',
                  width: 340,
                  render: (_v: string, record: Cfg) => (
                    <div className="system-config-editor">
                      {editor(record)}
                      {CONFIG_META[record.key]?.hint && <span>{CONFIG_META[record.key].hint}</span>}
                    </div>
                  ),
                },
                {
                  title: '说明',
                  dataIndex: 'description',
                  width: 270,
                  render: (description: string, record: Cfg) => (
                    <div className="system-config-description">
                      <span>{CONFIG_META[record.key]?.description || description || '-'}</span>
                      {record.updatedAt && <small>更新于 {fmtTime(record.updatedAt)}</small>}
                    </div>
                  ),
                },
              ]}
            />
          </Card>
        </>
      )}
    </div>
  )
}
