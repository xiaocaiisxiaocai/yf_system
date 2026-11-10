import {
  Avatar, Button, Empty, Popconfirm, Popover, Space, Spin, Tag, Typography,
} from '@arco-design/web-react'
import { IconCheck, IconDelete, IconRedo, IconUndo } from '@arco-design/web-react/icon'
import { memo, type ComponentType, type RefObject } from 'react'
import type { Message as Msg } from '../../api/types'
import { canWithdraw } from '../../hooks/useWithdrawClock'
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
  withdrawNow: number
  internalViewer: boolean
  onWithdraw: (id: number) => void | Promise<void>
  onRestore: (id: number) => void | Promise<void>
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
  withdrawNow: number
  internalViewer: boolean
  onWithdraw: Props['onWithdraw']
  onRestore: Props['onRestore']
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
  withdrawNow,
  internalViewer,
  onWithdraw,
  onRestore,
  MessageImages,
  ReceiptBody,
}: MessageRowProps) {
  const mine = m.senderId === userId
  const withdrawn = m.status === 'WITHDRAWN'
  // 厂商侧只看到撤回提示（撤回它的账号可恢复）；内部账号仍看到原文与图片，并标注已撤回。
  if (withdrawn && !internalViewer) {
    return <div
      className={`msg-item msg-item--withdrawn${m.id === targetId ? ' msg-item-target' : ''}`}
      data-message-id={m.id}
      aria-label={m.id === targetId ? '当前定位留言' : undefined}
      data-unread="false"
    >
      <Typography.Text type="secondary" className="message-row-withdrawn">
        {mine ? '你' : m.senderName}撤回了一条留言 · {fmtTime(m.createdAt)}
      </Typography.Text>
      {m.canRestore && <Popconfirm title="恢复这条留言？恢复后双方都能再次看到。" onOk={() => onRestore(m.id)}>
        <Button className="msg-secondary-action" size="mini" type="text" icon={<IconRedo />}>恢复</Button>
      </Popconfirm>}
    </div>
  }
  return <div
    className={`msg-item${m.id === targetId ? ' msg-item-target' : ''}${mine ? ' msg-item--mine' : ''}`}
    data-message-id={m.id}
    aria-label={m.id === targetId ? '当前定位留言' : undefined}
    data-unread={!m.readByMe && !mine && !withdrawn ? 'true' : 'false'}
  >
    <Space align="start">
      <Avatar size={32} className={m.senderType === 'SUPPLIER' ? 'msg-avatar msg-avatar--supplier' : 'msg-avatar'}>
        {m.senderName.slice(0, 1)}
      </Avatar>
      <div className="message-row-main">
        <Space size={8}>
          <Typography.Text bold>{m.senderName}</Typography.Text>
          {m.senderId === userId && <span className="msg-mine-badge">我</span>}
          <Tag size="small" color={m.senderType === 'SUPPLIER' ? 'purple' : 'arcoblue'}>
            {m.senderType === 'SUPPLIER' ? '供应商' : 'SAA'}
          </Tag>
          <Typography.Text type="secondary" className="message-row-time">{fmtTime(m.createdAt)}</Typography.Text>
          {withdrawn && <Tag size="small" color="orange">厂商已撤回</Tag>}
        </Space>
        <div className="message-row-content">{m.content}</div>
        {m.signerName && <div className="message-row-signature">— {m.signerName}</div>}
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
          {m.senderId === userId && <Button className="msg-secondary-action" size="mini" type="text" onClick={() => onOpenReceipt(m.id)}>回执详情</Button>}
          {canDelete && <Popconfirm title="删除这条留言？删除后双方均不可见。" onOk={() => onRemove(m.id)}>
            <Button className="msg-secondary-action" size="mini" type="text" status="danger" icon={<IconDelete />}>删除</Button>
          </Popconfirm>}
          {mine && canWithdraw(m.withdrawableUntil, withdrawNow) && <Popconfirm title="撤回这条留言？撤回后双方只能看到撤回提示。" onOk={() => onWithdraw(m.id)}>
            <Button className="msg-secondary-action" size="mini" type="text" status="warning" icon={<IconUndo />}>撤回</Button>
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
  withdrawNow,
  internalViewer,
  onWithdraw,
  onRestore,
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
                withdrawNow={withdrawNow}
                internalViewer={internalViewer}
                onWithdraw={onWithdraw}
                onRestore={onRestore}
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
