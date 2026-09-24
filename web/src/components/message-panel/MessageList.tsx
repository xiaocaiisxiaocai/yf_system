import {
  Avatar, Button, Empty, Popconfirm, Popover, Space, Spin, Tag, Typography,
} from '@arco-design/web-react'
import { IconCheck, IconDelete } from '@arco-design/web-react/icon'
import { memo, type ComponentType, type RefObject } from 'react'
import type { Message as Msg } from '../../api/types'
import './MessageList.css'

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

interface MessageRowProps {
  message: Msg
  targetId?: number
  userId?: number
  watermarkEmployeeNo?: string
  watermarkRealName?: string
  canDelete: boolean
  receiptRefresh: number
  fmtTime: Props['fmtTime']
  onReceiveReceipt: Props['onReceiveReceipt']
  onOpenReceipt: Props['onOpenReceipt']
  onRemove: Props['onRemove']
  MessageImages: Props['MessageImages']
  ReceiptBody: Props['ReceiptBody']
}

const MessageRow = memo(function MessageRow({
  message: m,
  targetId,
  userId,
  watermarkEmployeeNo,
  watermarkRealName,
  canDelete,
  receiptRefresh,
  fmtTime,
  onReceiveReceipt,
  onOpenReceipt,
  onRemove,
  MessageImages,
  ReceiptBody,
}: MessageRowProps) {
  return <div
    className={`msg-item${m.id === targetId ? ' msg-item-target' : ''}`}
    data-message-id={m.id}
    aria-label={m.id === targetId ? '当前定位留言' : undefined}
    data-unread={!m.readByMe && m.senderId !== userId ? 'true' : 'false'}
  >
    <Space align="start">
      <Avatar size={32} style={{ background: m.senderType === 'SUPPLIER' ? '#7b61ff' : '#165dff' }}>
        {m.senderName.slice(0, 1)}
      </Avatar>
      <div className="message-row-main">
        <Space size={8}>
          <Typography.Text bold>{m.senderName}</Typography.Text>
          <Tag size="small" color={m.senderType === 'SUPPLIER' ? 'purple' : 'arcoblue'}>
            {m.senderType === 'SUPPLIER' ? '供应商' : '公司'}
          </Tag>
          <Tag size="small" color="gray">项目留言</Tag>
          <Typography.Text type="secondary" className="message-row-time">{fmtTime(m.createdAt)}</Typography.Text>
        </Space>
        <div className="message-row-content">{m.content}</div>
        {!!m.images?.length && <MessageImages messageId={m.id} images={m.images} watermarkEmployeeNo={watermarkEmployeeNo} watermarkRealName={watermarkRealName} />}
        <div className="message-read-marker" aria-hidden="true" />
        <div className="message-row-actions">
          <Popover
            content={<div className="message-receipt-popover">
              <Typography.Text bold className="message-receipt-title">已读协作账号</Typography.Text>
              <ReceiptBody id={m.id} refreshKey={receiptRefresh} onLoaded={onReceiveReceipt} />
            </div>}
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
          {m.senderId === userId && <Button size="mini" type="text" onClick={() => onOpenReceipt(m.id)}>回执详情</Button>}
          {canDelete && <Popconfirm title="删除这条留言？删除后双方均不可见。" onOk={() => onRemove(m.id)}>
            <Button size="mini" type="text" status="danger" icon={<IconDelete />}>删除</Button>
          </Popconfirm>}
        </div>
      </div>
    </Space>
  </div>
})

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
    <div ref={listRef as RefObject<HTMLDivElement>} className="message-scroll-area" tabIndex={0} aria-label="留言列表">
      {loadError ? (
        <div className="message-load-error">
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={onRetryInitial}>重试</Button>
        </div>
      ) : (
        <Spin loading={loading && list.length === 0} style={{ width: '100%' }}>
          {list.length === 0 && !loading && !hasMore ? (
            <Empty description="暂无留言" />
          ) : (
            <div>
              {list.map((message) => <MessageRow
                key={message.id}
                message={message}
                targetId={targetId}
                userId={userId}
                watermarkEmployeeNo={watermarkEmployeeNo}
                watermarkRealName={watermarkRealName}
                canDelete={canDelete}
                receiptRefresh={receiptRefresh}
                fmtTime={fmtTime}
                onReceiveReceipt={onReceiveReceipt}
                onOpenReceipt={onOpenReceipt}
                onRemove={onRemove}
                MessageImages={MessageImages}
                ReceiptBody={ReceiptBody}
              />)}
              {appendError && (
                <div className="message-append-error">
                  <Typography.Text type="error">加载失败</Typography.Text>
                  <Button size="small" onClick={onLoadMore}>重试</Button>
                </div>
              )}
              {hasMore && (
                <div className="message-load-more">
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
