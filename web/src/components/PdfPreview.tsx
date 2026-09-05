import { useEffect, useState } from 'react'
import { Empty, Spin } from '@arco-design/web-react'
import http from '../api/client'

export default function PdfPreview({ fileId }: { fileId: number }) {
  const [result, setResult] = useState<{ fileId: number; url: string | null; failed: boolean }>({
    fileId,
    url: null,
    failed: false,
  })

  useEffect(() => {
    let active = true
    let obj: string | null = null
    http
      .get(`/files/${fileId}/content`, { responseType: 'blob' })
      .then((r) => {
        if (!active) return
        obj = URL.createObjectURL(new Blob([r.data], { type: 'application/pdf' }))
        setResult({ fileId, url: obj, failed: false })
      })
      .catch(() => {
        if (active) setResult({ fileId, url: null, failed: true })
      })
    return () => {
      active = false
      if (obj) URL.revokeObjectURL(obj)
    }
  }, [fileId])

  const current = result.fileId === fileId ? result : { fileId, url: null, failed: false }

  if (current.failed)
    return (
      <div style={{ padding: 60 }}>
        <Empty description="PDF 加载失败，请尝试下载后查看" />
      </div>
    )
  if (!current.url)
    return (
      <div style={{ textAlign: 'center', padding: 60 }}>
        <Spin size={32} />
      </div>
    )
  return <iframe className="pdf-frame" src={current.url} title="PDF 预览" />
}
