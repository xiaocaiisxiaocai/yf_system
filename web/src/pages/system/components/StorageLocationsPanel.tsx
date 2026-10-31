import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, Button, Input, Message, Modal, Popconfirm, Progress, Space, Spin, Table, Tag, Typography } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../../../api/client'
import type { ApiResponses } from '../../../api/types'
import { fmtSize } from '../../../api/types'
import LoadError from '../../../components/LoadError'

type StorageLocation = ApiResponses['GET /admin/storage-locations']['items'][number]

const POLL_MS = 3000

const PHASE_TEXT: Record<string, string> = {
  COPYING: '复制校验中',
  WAITING: '等待上传完成',
  DELETING: '清理旧目录',
}

function statusTag(location: StorageLocation) {
  if (location.status === 'FAILED') return <Tag color="red">迁移失败</Tag>
  if (location.status === 'MIGRATING') return <Tag color="arcoblue">{PHASE_TEXT[location.phase ?? ''] ?? '迁移中'}</Tag>
  if (location.pendingPath) return <Tag color="orange">重启后生效</Tag>
  if (!location.path) return <Tag>未配置</Tag>
  return <Tag color="green">正常</Tag>
}

function progressPercent(location: StorageLocation) {
  if (location.phase === 'DELETING') {
    return location.totalFiles > 0 ? Math.min(100, Math.round((location.deletedFiles / location.totalFiles) * 100)) : 100
  }
  return location.totalBytes > 0 ? Math.min(100, Math.round((location.copiedBytes / location.totalBytes) * 100)) : 0
}

/** 系统参数 → 存储位置：三类存储的生效路径与在线迁移（存储路径页面配置与在线迁移方案）。 */
export default function StorageLocationsPanel() {
  const [items, setItems] = useState<StorageLocation[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [editing, setEditing] = useState<StorageLocation | null>(null)
  const [target, setTarget] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [acting, setActing] = useState<string | null>(null)
  const active = useRef(true)

  const fetchLocations = useCallback(async (quiet: boolean) => (
    await http.get<ApiResponses['GET /admin/storage-locations']>('/admin/storage-locations',
      { quietNetworkError: quiet } as QuietRequestConfig)
  ).data.items, [])

  const load = useCallback((quiet = false) => fetchLocations(quiet).then((next) => {
    if (!active.current) return
    setItems(next)
    setLoadError(false)
  }).catch(() => {
    if (active.current && !quiet) setLoadError(true)
  }).finally(() => {
    if (active.current) setLoading(false)
  }), [fetchLocations])

  useEffect(() => {
    active.current = true
    fetchLocations(false).then((next) => {
      if (!active.current) return
      setItems(next)
      setLoadError(false)
    }).catch(() => {
      if (active.current) setLoadError(true)
    }).finally(() => {
      if (active.current) setLoading(false)
    })
    return () => { active.current = false }
  }, [fetchLocations])

  const migrating = items.some((item) => item.status === 'MIGRATING')
  useEffect(() => {
    if (!migrating) return
    const timer = window.setInterval(() => { void load(true) }, POLL_MS)
    return () => window.clearInterval(timer)
  }, [load, migrating])

  const busy = items.some((item) => item.status !== 'IDLE' || item.previousPath)

  const openEdit = (location: StorageLocation) => {
    setTarget('')
    setEditing(location)
  }

  const submit = async () => {
    if (!editing || submitting) return
    setSubmitting(true)
    try {
      await http.post<ApiResponses['POST /admin/storage-locations/{kind}/migrate']>(
        `/admin/storage-locations/${editing.kind}/migrate`, { path: target.trim() })
      Message.success(editing.path ? '已开始迁移' : '路径已保存')
      setEditing(null)
      await load(true)
    } catch { /* 拦截器已提示，保留输入以便修改后重试。 */ }
    finally { setSubmitting(false) }
  }

  const act = async (location: StorageLocation, action: 'cancel' | 'retry') => {
    if (acting) return
    setActing(location.kind)
    try {
      if (action === 'cancel') {
        await http.post<ApiResponses['POST /admin/storage-locations/{kind}/cancel']>(`/admin/storage-locations/${location.kind}/cancel`)
      } else {
        await http.post<ApiResponses['POST /admin/storage-locations/{kind}/retry']>(`/admin/storage-locations/${location.kind}/retry`)
      }
      Message.success(action === 'cancel' ? '已取消，正在迁回原目录' : '已重新开始迁移')
      await load(true)
    } catch { /* 拦截器已提示。 */ }
    finally { setActing(null) }
  }

  if (loadError) return <LoadError onRetry={() => { setLoading(true); void load() }} />
  if (loading) return <Spin loading style={{ width: '100%', minHeight: 120 }} />

  const columns = [
    { title: '存储', dataIndex: 'label', width: 140 },
    {
      title: '路径',
      render: (_: unknown, row: StorageLocation) => (
        <div className="storage-location-paths">
          <Typography.Text copyable={Boolean(row.path)}>{row.path || '-'}</Typography.Text>
          {row.previousPath && <Typography.Text type="secondary">原目录：{row.previousPath}</Typography.Text>}
          {row.pendingPath && <Typography.Text type="secondary">待生效：{row.pendingPath}</Typography.Text>}
        </div>
      ),
    },
    { title: '状态', width: 130, render: (_: unknown, row: StorageLocation) => statusTag(row) },
    {
      title: '进度',
      width: 220,
      render: (_: unknown, row: StorageLocation) => row.status === 'IDLE' ? (
        row.freeBytes != null ? <Typography.Text type="secondary">剩余 {fmtSize(row.freeBytes)}</Typography.Text> : '-'
      ) : (
        <div>
          <Progress size="small" percent={progressPercent(row)} status={row.status === 'FAILED' ? 'error' : undefined} />
          <Typography.Text type="secondary">
            {row.phase === 'DELETING'
              ? `已清理 ${row.deletedFiles}/${row.totalFiles} 个文件`
              : `${row.copiedFiles}/${row.totalFiles} 个文件，${fmtSize(row.copiedBytes)}/${fmtSize(row.totalBytes)}`}
          </Typography.Text>
        </div>
      ),
    },
    {
      title: '操作',
      width: 200,
      render: (_: unknown, row: StorageLocation) => (
        <Space size={4}>
          {row.status === 'IDLE' && !row.previousPath && (
            <Button type="text" size="small" disabled={busy} onClick={() => openEdit(row)}>
              {row.path || row.pendingPath ? '更换路径' : '设置路径'}
            </Button>
          )}
          {row.status === 'FAILED' && (
            <Button type="text" size="small" loading={acting === row.kind} onClick={() => act(row, 'retry')}>重试</Button>
          )}
          {row.previousPath && row.kind !== 'LOG' && row.phase !== 'DELETING' && (
            <Popconfirm title="取消迁移？已写入新目录的内容将迁回原目录" onOk={() => act(row, 'cancel')}>
              <Button type="text" size="small" status="danger" loading={acting === row.kind}>取消迁移</Button>
            </Popconfirm>
          )}
        </Space>
      ),
    },
  ]

  return (
    <div className="system-storage-locations">
      {items.filter((item) => item.lastError).map((item) => (
        <Alert key={item.kind} type={item.status === 'FAILED' ? 'error' : 'warning'} style={{ marginBottom: 12 }}
          content={`${item.label}：${item.lastError}`} />
      ))}
      <Table rowKey="kind" data={items} columns={columns} pagination={false} border={false} />
      <Modal
        title={editing ? `${editing.path ? '更换' : '设置'}${editing.label}路径` : ''}
        visible={Boolean(editing)}
        confirmLoading={submitting}
        okText={editing?.path ? '开始迁移' : '保存'}
        okButtonProps={{ disabled: !target.trim() }}
        onOk={submit}
        onCancel={() => { if (!submitting) setEditing(null) }}
        unmountOnExit
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Input
            aria-label="新目录路径"
            placeholder="例如 D:\YfData\storage-new"
            value={target}
            disabled={submitting}
            onChange={setTarget}
            onPressEnter={submit}
          />
          {editing?.path ? (
            <Typography.Text type="secondary">
              新目录须为空，并已授予 IIS 应用池账号读写权限。迁移期间系统照常使用；全部文件复制校验完成后，将自动删除原目录中已迁移的文件。
            </Typography.Text>
          ) : editing?.kind === 'OEM' ? (
            <Typography.Text type="secondary">首次设置 OEM 文件存储需重启站点后生效。</Typography.Text>
          ) : null}
        </Space>
      </Modal>
    </div>
  )
}
