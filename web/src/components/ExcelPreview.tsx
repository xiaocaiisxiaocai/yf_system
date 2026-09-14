import { useEffect, useRef, useState } from 'react'
import { Button, Result, Spin } from '@arco-design/web-react'
import http from '../api/client'
import viewerHtml from '../../generated/excel-viewer.html?raw'

function ExcelDocument({ fileId }: { fileId: number }) {
  const frame = useRef<HTMLIFrameElement>(null)
  const [channel] = useState(() => Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join(''))
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading')
  const send = useRef<() => void>(() => {})
  const frameLoaded = useRef(false)
  const html = viewerHtml.replaceAll('YF_VIEWER_NONCE', channel)

  useEffect(() => {
    let active = true
    let buffer: ArrayBuffer | undefined
    let sent = false
    const controller = new AbortController()
    const timer = setTimeout(() => { if (active) setState('error') }, 60000)
    const receive = (event: MessageEvent) => {
      if (!active || event.source !== frame.current?.contentWindow || event.data?.channel !== channel) return
      if (event.data.type === 'excel:rendered') { clearTimeout(timer); setState('ready') }
      if (event.data.type === 'excel:error') { clearTimeout(timer); setState('error') }
    }
    window.addEventListener('message', receive)
    send.current = () => {
      if (!active || !frameLoaded.current || !buffer || sent) return
      const signature = new Uint8Array(buffer, 0, Math.min(buffer.byteLength, 4))
      const xls = signature[0] === 0xd0 && signature[1] === 0xcf
      const xlsx = signature[0] === 0x50 && signature[1] === 0x4b
      if (!xls && !xlsx) { clearTimeout(timer); setState('error'); return }
      sent = true
      frame.current?.contentWindow?.postMessage({ type: 'excel:load', channel, buffer, xls }, '*', [buffer])
      buffer = undefined
    }
    void http.get<ArrayBuffer>(`/files/${fileId}/content`, { responseType: 'arraybuffer', signal: controller.signal })
      .then(response => { if (active) { buffer = response.data; send.current() } })
      .catch(() => { if (active) { clearTimeout(timer); setState('error') } })
    return () => { active = false; clearTimeout(timer); controller.abort(); window.removeEventListener('message', receive); send.current = () => {} }
  }, [fileId, channel, attempt])

  return <div className="excel-source-preview">
    <iframe key={attempt} ref={frame} className="excel-source-frame" title="Excel 预览内容"
      sandbox="allow-scripts" referrerPolicy="no-referrer" srcDoc={html}
      onLoad={() => { frameLoaded.current = true; send.current() }}
      style={{ visibility: state === 'ready' ? 'visible' : 'hidden' }} />
    {state === 'loading' && <div className="excel-source-status" role="status"><Spin />正在渲染工作簿…</div>}
    {state === 'error' && <div className="excel-source-status"><Result status="error" title="Excel 预览失败"
      subTitle="文件可能损坏、受密码保护，或包含暂不支持的内容。"
      extra={<Button onClick={() => { frameLoaded.current = false; setState('loading'); setAttempt(value => value + 1) }}>重试预览</Button>} /></div>}
  </div>
}

export default function ExcelPreview({ fileId }: { fileId: number }) {
  return <ExcelDocument key={fileId} fileId={fileId} />
}
