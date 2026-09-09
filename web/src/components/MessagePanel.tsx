import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Avatar, Button, Drawer, Empty, Input, List, Popconfirm, Popover, Select, Space, Spin, Tag, Typography,
} from '@arco-design/web-react'
import { IconCheck, IconDelete, IconSend } from '@arco-design/web-react/icon'
import http from '../api/client'
import { useAuth } from '../store/auth'
import { type Message as Msg, type Round, fmtTime } from '../api/types'

interface Props {
  projectId: number
  projectStatus: string
  rounds: Round[]
  onRead?: () => void
}

interface Reader {
  userId: number
  realName: string
  userType: string
  readAt?: string | null
}

export default function MessagePanel({ projectId, projectStatus, rounds, onRead }: Props) {
  const [list, setList] = useState<Msg[]>([])
  const [total, setTotal] = useState(0)
  const [hasMore, setHasMore] = useState(false)
  const [page, setPage] = useState(1)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [appendError, setAppendError] = useState(false)
  const [content, setContent] = useState('')
  const [roundId, setRoundId] = useState<number | undefined>()
  const [filterRound, setFilterRound] = useState<number | undefined>()
  const [receipt, setReceipt] = useState<{ id: number; readers: Reader[]; unread: Reader[] } | null>(null)
  const { hasPerm, user } = useAuth()
  const listRef = useRef<HTMLDivElement>(null)
  const loadSeq = useRef(0)
  const receiptSeq = useRef(0)
  const cursor = useRef<number | undefined>()
  const markingRead = useRef(new Set<number>())
  const [sending, setSending] = useState(false)

  const roundMap = new Map(rounds.map((r) => [r.id, r.roundNo]))
  const pendingRounds = rounds.filter((r) => r.status === 'PENDING')
  const canWrite = hasPerm('message:create') && projectStatus !== 'COMPLETED' && projectStatus !== 'TERMINATED'

  const load = useCallback(
    async (p: number, append: boolean) => {
      const seq = ++loadSeq.current
      setLoading(true)
      if (append) setAppendError(false)
      else setLoadError(false)
      try {
        const r = await http.get(`/projects/${projectId}/messages`, {
          params: { page: p, pageSize: 20, roundId: filterRound, beforeId: append ? cursor.current : undefined },
        })
        if (seq !== loadSeq.current) return // 已有更新的请求在途，丢弃旧响应
        setTotal(r.data.total)
        cursor.current = r.data.list.at(-1)?.id
        setHasMore(r.data.list.length === 20)
        setList((current) => append ? [...current, ...r.data.list.filter((m: Msg) => !current.some((old) => old.id === m.id))] : r.data.list)
        setPage(p)
        setLoadError(false)
        setAppendError(false)
      } catch (error) {
        if (seq === loadSeq.current) {
          if (append) setAppendError(true)
          else setLoadError(true)
          if (append) throw error
        }
      } finally {
        if (seq === loadSeq.current) setLoading(false)
      }
    },
    [projectId, filterRound]
  )

  useEffect(() => {
    load(1, false)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterRound, projectId])

  // 只有留言实际进入可视区域后才上报已读，避免“加载第一页=全部已读”。
  useEffect(() => {
    const root = listRef.current
    if (!root || typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver(
      (entries) => {
        const ids = entries
          .filter((entry) => entry.isIntersecting && entry.intersectionRatio >= 0.6)
          .map((entry) => Number((entry.target as HTMLElement).closest<HTMLElement>('[data-message-id]')?.dataset.messageId))
          .filter((id) => Number.isFinite(id) && !markingRead.current.has(id))
        if (!ids.length) return
        ids.forEach((id) => markingRead.current.add(id))
        void http.post('/messages/read', { ids }).then(() => {
          setList((current) => current.map((m) => (ids.includes(m.id) ? { ...m, readByMe: true } : m)))
          onRead?.()
        }).catch(() => {
          ids.forEach((id) => markingRead.current.delete(id))
        })
      },
      { threshold: 0.6 }
    )
    // 观察正文末尾的小标记；长留言不可能有 60% 的整块高度同时进入视口。
    root.querySelectorAll<HTMLElement>('[data-unread="true"] .message-read-marker').forEach((node) => observer.observe(node))
    return () => observer.disconnect()
  }, [list, onRead])

  const send = async () => {
    const text = content.trim()
    if (!text || sending) return
    setSending(true)
    try {
      await http.post(`/projects/${projectId}/messages`, { content: text, roundId })
      setContent('')
      load(1, false)
    } finally {
      setSending(false)
    }
  }

  const openReceipt = async (id: number) => {
    const seq = ++receiptSeq.current
    try {
      const r = await http.get(`/messages/${id}/reads`)
      if (seq === receiptSeq.current) setReceipt({ id, readers: r.data.readers, unread: r.data.unread })
    } catch {
      // 请求失败时保持当前回执，避免错误响应清空正在查看的留言。
    }
  }

  const remove = async (id: number) => {
    await http.delete(`/messages/${id}`)
    setList((current) => current.filter((m) => m.id !== id))
    setTotal((current) => Math.max(0, current - 1))
  }

  return (
    <div>
      <div className="section-heading">
        <div>
          <h2>协作留言（{total}）</h2>
        </div>
        <Select
          allowClear
          placeholder="全部留言"
          style={{ width: 160 }}
          value={filterRound}
          onChange={(v) => setFilterRound(v as number | undefined)}
        >
          <Select.Option value={0}>仅项目级</Select.Option>
          {rounds.map((r) => (
            <Select.Option key={r.id} value={r.id}>
              第 {r.roundNo} 轮
            </Select.Option>
          ))}
        </Select>
      </div>

      {canWrite && (
        <div className="message-composer" style={{ display: 'flex', gap: 8, marginBottom: 16 }}>
          <Select
            allowClear
            placeholder="关联轮次（可选）"
            style={{ width: 150 }}
            value={roundId}
            onChange={(v) => setRoundId(v as number | undefined)}
          >
            {pendingRounds.map((r) => (
              <Select.Option key={r.id} value={r.id}>
                第 {r.roundNo} 轮
              </Select.Option>
            ))}
          </Select>
          <Input.TextArea
            placeholder="输入留言，Ctrl+Enter 发送"
            value={content}
            onChange={setContent}
            autoSize={{ minRows: 2, maxRows: 5 }}
            maxLength={4000}
            style={{ flex: 1 }}
            onKeyDown={(e) => {
              if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') send()
            }}
          />
          <Button type="primary" icon={<IconSend />} onClick={send} disabled={!content.trim()} loading={sending}>
            发送
          </Button>
        </div>
      )}

      {loadError ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={() => load(1, false)}>重试</Button>
        </div>
      ) : (
        <Spin loading={loading && page === 1} style={{ width: '100%' }}>
          {list.length === 0 && !loading && !hasMore ? (
            <Empty description="暂无留言" />
          ) : (
            <div ref={listRef}>
              {list.map((m) => (
              <div
                className="msg-item"
                key={m.id}
                data-message-id={m.id}
                data-unread={!m.readByMe && m.senderId !== user?.id ? 'true' : 'false'}
              >
                <Space align="start">
                  <Avatar
                    size={32}
                    style={{ background: m.senderType === 'SUPPLIER' ? '#7b61ff' : '#165dff' }}
                  >
                    {m.senderName.slice(0, 1)}
                  </Avatar>
                  <div style={{ flex: 1 }}>
                    <Space size={8}>
                      <Typography.Text bold>{m.senderName}</Typography.Text>
                      <Tag size="small" color={m.senderType === 'SUPPLIER' ? 'purple' : 'arcoblue'}>
                        {m.senderType === 'SUPPLIER' ? '供应商' : '公司'}
                      </Tag>
                      {m.roundId ? (
                        <Tag size="small">第 {roundMap.get(m.roundId) ?? '?'} 轮</Tag>
                      ) : (
                        <Tag size="small" color="gray">
                          项目级
                        </Tag>
                      )}
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {fmtTime(m.createdAt)}
                      </Typography.Text>
                    </Space>
                    <div style={{ marginTop: 4, whiteSpace: 'pre-wrap' }}>{m.content}</div>
                    <div className="message-read-marker" aria-hidden="true" />
                    <div style={{ marginTop: 4 }}>
                      <Popover
                        content={
                          <div style={{ maxWidth: 320 }}>
                            <Typography.Text bold style={{ fontSize: 12 }}>已读人员</Typography.Text>
                            <ReceiptBody id={m.id} />
                          </div>
                        }
                        trigger="click"
                      >
                        <Typography.Text
                          type={m.readCount >= m.totalCount && m.totalCount > 0 ? 'success' : 'secondary'}
                          style={{ fontSize: 12, cursor: 'pointer' }}
                        >
                          <IconCheck /> 已读 {m.readCount}/{m.totalCount}
                        </Typography.Text>
                      </Popover>
                      {m.senderId === user?.id && (
                        <Button size="mini" type="text" onClick={() => openReceipt(m.id)} style={{ marginLeft: 8 }}>
                          回执详情
                        </Button>
                      )}
                      {hasPerm('message:delete_any') && (
                        <Popconfirm title="删除这条留言？删除后双方均不可见。" onOk={() => remove(m.id)}>
                          <Button size="mini" type="text" status="danger" icon={<IconDelete />} style={{ marginLeft: 8 }}>
                            删除
                          </Button>
                        </Popconfirm>
                      )}
                    </div>
                  </div>
                </Space>
              </div>
              ))}
              {appendError && (
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8, padding: 12 }}>
                  <Typography.Text type="error">加载失败</Typography.Text>
                  <Button size="small" onClick={() => load(page + 1, true)}>重试</Button>
                </div>
              )}
              {hasMore && (
                <div style={{ textAlign: 'center', padding: 12 }}>
                  <Button onClick={() => load(page + 1, true)} loading={loading}>
                    加载更多（{list.length}/{total}）
                  </Button>
                </div>
              )}
            </div>
          )}
        </Spin>
      )}

      <Drawer
        width={420}
        title="已读回执"
        visible={!!receipt}
        onCancel={() => {
          receiptSeq.current += 1
          setReceipt(null)
        }}
        footer={null}
      >
        {receipt && (
          <>
            <Typography.Text bold>已读（{receipt.readers.length}）</Typography.Text>
            <List
              size="small"
              dataSource={receipt.readers}
              render={(r) => (
                <List.Item key={r.userId} extra={fmtTime(r.readAt)}>
                  {r.realName}
                  <Tag size="small" color={r.userType === 'SUPPLIER' ? 'purple' : 'arcoblue'} style={{ marginLeft: 6 }}>
                    {r.userType === 'SUPPLIER' ? '供应商' : '公司'}
                  </Tag>
                </List.Item>
              )}
            />
            <Typography.Text bold style={{ display: 'block', marginTop: 12 }}>
              未读（{receipt.unread.length}）
            </Typography.Text>
            <List
              size="small"
              dataSource={receipt.unread}
              render={(r) => (
                <List.Item key={r.userId}>
                  {r.realName}
                  <Tag size="small" color={r.userType === 'SUPPLIER' ? 'purple' : 'arcoblue'} style={{ marginLeft: 6 }}>
                    {r.userType === 'SUPPLIER' ? '供应商' : '公司'}
                  </Tag>
                </List.Item>
              )}
            />
          </>
        )}
      </Drawer>
    </div>
  )
}

/** 气泡内联的已读名单（轻量版） */
function ReceiptBody({ id }: { id: number }) {
  const [names, setNames] = useState<string[]>([])
  useEffect(() => {
    http.get(`/messages/${id}/reads`).then((r) => setNames(r.data.readers.map((x: Reader) => x.realName)))
  }, [id])
  return <div style={{ fontSize: 12 }}>{names.length ? names.join('、') : '暂无'}</div>
}
