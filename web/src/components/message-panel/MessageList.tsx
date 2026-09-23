import {
  Avatar, Button, Empty, Popconfirm, Popover, Space, Spin, Tag, Typography,
} from '@arco-design/web-react'
import { IconCheck, IconDelete } from '@arco-design/web-react/icon'
import type { ComponentType, RefObject } from 'react'
import type { Message as Msg } from '../../api/types'

interface Props {
  list: Msg[]
  total: number
  hasMore: boolean
  loading: boolean
  loadError: boolean
  appendError: boolean
  targetId?: number
  userId?: number
  watermarkEmployeeNo?: string
  watermarkRealName?: string
  canDelete: boolean
  receiptRefresh: number
  listRef: RefObject<HTMLDivElement | null>
  fmtTime: (value?: string | null) => string
  onRetryInitial: () => void
  onLoadMore: () => void
  onReceiveReceipt: (id: number, readCount: number, totalCount: number) => void
  onOpenReceipt: (id: number) => void
  onRemove: (id: number) => void | Promise<void>
  MessageImages: ComponentType<{
    messageId: number
    images: Msg['images']
    watermarkEmployeeNo?: string
    watermarkRealName?: string
  }>
  ReceiptBody: ComponentType<{
    id: number
    refreshKey?: number
    onLoaded?: (id: number, readCount: number, totalCount: number) => void
  }>
}

export function MessageList({
  list,
  total,
  hasMore,
  loading,
  loadError,
  appendError,
  targetId,
  userId,
  watermarkEmployeeNo,
  watermarkRealName,
  canDelete,
  receiptRefresh,
  listRef,
  fmtTime,
  onRetryInitial,
  onLoadMore,
  onReceiveReceipt,
  onOpenReceipt,
  onRemove,
  MessageImages,
  ReceiptBody,
}: Props) {
  return (
    <div className="message-scroll-area" tabIndex={0} aria-label="留言列表">
      {loadError ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={onRetryInitial}>重试</Button>
        </div>
      ) : (
        <Spin loading={loading && list.length === 0} style={{ width: '100%' }}>
          {list.length === 0 && !loading && !hasMore ? (
            <Empty description="暂无留言" />
          ) : (
            <div ref={listRef as unknown as RefObject<HTMLDivElement>}>
              {list.map((m) => (
              <div
                className="msg-item"
                key={m.id}
                data-message-id={m.id}
                aria-label={m.id === targetId ? '当前定位留言' : undefined}
                style={m.id === targetId ? { borderLeft: '3px solid rgb(var(--primary-6))', paddingLeft: 12, background: 'var(--color-fill-1)' } : undefined}
                data-unread={!m.readByMe && m.senderId !== userId ? 'true' : 'false'}
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
                      <Tag size="small" color="gray">项目留言</Tag>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {fmtTime(m.createdAt)}
                      </Typography.Text>
                    </Space>
                    <div style={{ marginTop: 4, whiteSpace: 'pre-wrap' }}>{m.content}</div>
                    {!!m.images?.length && <MessageImages messageId={m.id} images={m.images} watermarkEmployeeNo={watermarkEmployeeNo} watermarkRealName={watermarkRealName} />}
                    <div className="message-read-marker" aria-hidden="true" />
                    <div style={{ marginTop: 4 }}>
                      <Popover
                        content={
                          <div style={{ maxWidth: 320 }}>
                            <Typography.Text bold style={{ fontSize: 12 }}>已读协作账号</Typography.Text>
                            <ReceiptBody id={m.id} refreshKey={receiptRefresh} onLoaded={onReceiveReceipt} />
                          </div>
                        }
                        trigger="click"
                        triggerProps={{ escToClose: true, unmountOnExit: true }}
                      >
                        <Button
                          size="mini"
                          type="text"
                          status={m.readCount >= m.totalCount && m.totalCount > 0 ? 'success' : undefined}
                          aria-label={`查看留言回执：${m.readCount}/${m.totalCount}`}
                          icon={<IconCheck />}
                        >
                          已读 {m.readCount}/{m.totalCount}
                        </Button>
                      </Popover>
                      {m.senderId === userId && (
                        <Button size="mini" type="text" onClick={() => onOpenReceipt(m.id)} style={{ marginLeft: 8 }}>
                          回执详情
                        </Button>
                      )}
                      {canDelete && (
                        <Popconfirm title="删除这条留言？删除后双方均不可见。" onOk={() => onRemove(m.id)}>
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
                  <Button size="small" onClick={onLoadMore}>重试</Button>
                </div>
              )}
              {hasMore && (
                <div style={{ textAlign: 'center', padding: 12 }}>
                  <Button onClick={onLoadMore} loading={loading}>
                    加载更多（{list.length}/{total}）
                  </Button>
                </div>
              )}
            </div>
          )}
        </Spin>
      )}
    </div>
  )
}
