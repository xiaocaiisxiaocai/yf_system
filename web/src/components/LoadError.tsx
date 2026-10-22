import { Button, Typography } from '@arco-design/web-react'

/** 列表或页面首次加载失败时的统一占位：错误文案 + 重试。 */
export default function LoadError({ message = '加载失败', onRetry }: { message?: string; onRetry: () => void }) {
  return (
    <div className="page-load-error" role="alert">
      <Typography.Text type="error">{message}</Typography.Text>
      <Button size="small" onClick={onRetry}>重试</Button>
    </div>
  )
}
