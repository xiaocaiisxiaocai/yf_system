import { Button, Drawer, List, Space, Tag, Typography } from '@arco-design/web-react'

interface Reader {
  userId: number
  realName: string
  userType: string
  readAt?: string | null
}

interface Receipt {
  id: number
  readers: Reader[]
  unread: Reader[]
}

interface ReceiptRequest {
  id: number
  loading: boolean
  error: boolean
}

interface Props {
  receipt: Receipt | null
  receiptRequest: ReceiptRequest | null
  fmtTime: (value?: string | null) => string
  onCancel: () => void
  onRetry: (id: number) => void
}

export function MessageReceiptDrawer({ receipt, receiptRequest, fmtTime, onCancel, onRetry }: Props) {
  return (
    <Drawer
      width={420}
      title="留言已读回执"
      visible={!!receipt || !!receiptRequest}
      onCancel={onCancel}
      footer={null}
    >
      {receiptRequest?.loading && !receipt && <Typography.Text type="secondary">回执加载中…</Typography.Text>}
      {receiptRequest?.error && !receipt && (
        <Space>
          <Typography.Text type="error">回执加载失败</Typography.Text>
          <Button size="small" onClick={() => onRetry(receiptRequest.id)}>重试</Button>
        </Space>
      )}
      {receipt && (
        <>
          <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 12 }}>
            统计项目负责人和关联供应商的全部启用账号，不包含仅有全局查看权限的人员。
          </Typography.Text>
          <Typography.Text bold>已读（{receipt.readers.length}）</Typography.Text>
          <List
            size="small"
            dataSource={receipt.readers}
            render={(reader) => (
              <List.Item key={reader.userId} extra={fmtTime(reader.readAt)}>
                {reader.realName}
                <Tag size="small" color={reader.userType === 'SUPPLIER' ? 'purple' : 'arcoblue'} style={{ marginLeft: 6 }}>
                  {reader.userType === 'SUPPLIER' ? '供应商' : '公司'}
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
            render={(reader) => (
              <List.Item key={reader.userId}>
                {reader.realName}
                <Tag size="small" color={reader.userType === 'SUPPLIER' ? 'purple' : 'arcoblue'} style={{ marginLeft: 6 }}>
                  {reader.userType === 'SUPPLIER' ? '供应商' : '公司'}
                </Tag>
              </List.Item>
            )}
          />
        </>
      )}
    </Drawer>
  )
}
