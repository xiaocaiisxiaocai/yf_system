import { useEffect, useMemo, useRef, useState } from 'react'
import { Button, Result, Spin } from '@arco-design/web-react'
import http, { type QuietRequestConfig } from '../api/client'
import viewerHtml from '../../generated/excel-viewer.html?raw'
import { createPreviewDeadline } from './previewDeadline'

function ExcelDocument({ fileId, ext }: { fileId: number; ext?: string }) {
  const frame = useRef<HTMLIFrameElement>(null)
  const [channel] = useState(() => Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join(''))
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading')
  const send = useRef<() => void>(() => {})
  const frameLoaded = useRef(false)
  const html = useMemo(() => viewerHtml.replaceAll('YF_VIEWER_NONCE', channel), [channel])

  useEffect(() => {
    let active = true
    let buffer: ArrayBuffer | undefined
    let sent = false
    const controller = new AbortController()
    const deadline = createPreviewDeadline(() => { controller.abort(); if (active) setState('error') })
    const receive = (event: MessageEvent) => {
      if (!active || event.source !== frame.current?.contentWindow || event.data?.channel !== channel) return
      if (event.data.type === 'excel:rendered') { deadline.clear(); setState('ready') }
      if (event.data.type === 'excel:error') { deadline.clear(); setState('error') }
    }
    window.addEventListener('message', receive)
    send.current = () => {
      if (!active || !frameLoaded.current || !buffer || sent) return
      const signature = new Uint8Array(buffer, 0, Math.min(buffer.byteLength, 4))
      const xls = signature[0] === 0xd0 && signature[1] === 0xcf
      const xlsx = signature[0] === 0x50 && signature[1] === 0x4b
      if (!xls && !xlsx) { deadline.clear(); setState('error'); return }
      // 旧版 .xls（OLE2）、二进制 .xlsb（ZIP 内为 BIFF12，ExcelJS 会静默解析成空表）和启用宏的 .xlsm
      // （工作簿声明为 macroEnabled，ExcelJS 找不到工作簿而报错）都先经 SheetJS 转成 xlsx。
      const convert = xls || ['xlsb', 'xlsm'].includes(ext?.toLowerCase() ?? '')
      sent = true
      frame.current?.contentWindow?.postMessage({ type: 'excel:load', channel, buffer, xls: convert }, '*', [buffer])
      buffer = undefined
    }
    void http.get<ArrayBuffer>(`/files/${fileId}/content`, {
      responseType: 'arraybuffer', signal: controller.signal, quietNetworkError: true,
      timeout: 0, onDownloadProgress: (event) => deadline.progress(event.loaded),
    } as QuietRequestConfig)
      .then(response => { if (active) { deadline.downloaded(); buffer = response.data; send.current() } })
      .catch(() => { if (active) { deadline.clear(); setState('error') } })
    return () => { active = false; deadline.clear(); controller.abort(); window.removeEventListener('message', receive); send.current = () => {} }
  }, [fileId, ext, channel, attempt])

  return <div className="excel-source-preview">
    <iframe key={attempt} ref={frame} className="excel-source-frame" title="Excel 预览内容"
      sandbox="allow-scripts" referrerPolicy="no-referrer" srcDoc={html}
      onLoad={() => { frameLoaded.current = true; send.current() }}
      style={{ visibility: state === 'ready' ? 'visible' : 'hidden' }} />
    {state === 'loading' && <div className="excel-source-status" role="status"><Spin />正在渲染工作簿…</div>}
    {state === 'error' && <div className="excel-source-status" role="alert"><Result status="error" title="Excel 预览失败"
      subTitle="文件可能损坏、受密码保护，或包含暂不支持的内容。"
      extra={<Button onClick={() => { frameLoaded.current = false; setState('loading'); setAttempt(value => value + 1) }}>重试预览</Button>} /></div>}
  </div>
}

export default function ExcelPreview({ fileId, ext }: { fileId: number; ext?: string }) {
  return <ExcelDocument key={fileId} fileId={fileId} ext={ext} />
}
