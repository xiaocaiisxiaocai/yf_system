import { Spin } from '@arco-design/web-react'

export default function PreviewFallback({ tip = '加载预览…' }: { tip?: string }) {
  return (
    <div className="pdf-preview-status">
      <Spin tip={tip} />
    </div>
  )
}
