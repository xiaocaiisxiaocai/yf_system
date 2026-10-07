import { Suspense, lazy, useEffect, useState } from 'react'
import { Button, Modal, Result, Spin } from '@arco-design/web-react'
import { IconClose } from '@arco-design/web-react/icon'
import { PreviewWatermark } from '../../components/PreviewWatermark'
import { useAuth } from '../../store/auth'
import { usePortalAuth } from '../api/portalSession'
import type { TransferFile } from '../api/types'
import { useOem } from '../OemContext'
import { oemPreviewKind } from './oemPreview'

const PdfPreview = lazy(() => import('../../components/PdfPreview'))
const ImagePreview = lazy(() => import('../../components/ImagePreview'))

/** Watermark identity of whoever is looking: the internal user or the OEM vendor account. */
function useViewerIdentity(): { employeeNo?: string; realName?: string } {
  const { realm } = useOem()
  const internalUser = useAuth((state) => state.user)
  const portalAccount = usePortalAuth((state) => state.account)
  const viewer = realm === 'oem' ? portalAccount : internalUser
  return { employeeNo: viewer?.employeeNo, realName: viewer?.realName }
}

function OemImage({ file }: { file: TransferFile }) {
  const { api } = useOem()
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<{ url?: string; failed?: boolean }>({})
  useEffect(() => {
    const controller = new AbortController()
    let url = ''
    api.previewBlob(file.id, controller.signal).then((blob) => {
      if (controller.signal.aborted) return
      url = URL.createObjectURL(blob)
      setState({ url })
    }).catch(() => {
      if (!controller.signal.aborted) setState({ failed: true })
    })
    return () => {
      controller.abort()
      if (url) URL.revokeObjectURL(url)
    }
  }, [api, file.id, attempt])
  if (state.failed) {
    return <Result status="error" title="图片预览失败" subTitle="读取失败，请重试。"
      extra={<Button onClick={() => { setState({}); setAttempt((value) => value + 1) }}>重试图片预览</Button>} />
  }
  if (!state.url) return <div role="status" style={{ textAlign: 'center', padding: 60 }}><Spin /> 正在加载图片…</div>
  return <ImagePreview sourceUrl={state.url} name={file.originalName} />
}

/**
 * In-page OEM attachment preview with the same watermark (employee no. + name + open time)
 * and modal layout as the collaboration file preview. Bytes come from the OEM content endpoint.
 */
export default function OemFilePreview({ file, onClose }: { file: TransferFile | null; onClose: () => void }) {
  const { api } = useOem()
  const { employeeNo, realName } = useViewerIdentity()
  const [toolbar, setToolbar] = useState<HTMLDivElement | null>(null)
  const kind = file ? oemPreviewKind(file.ext) : null
  return (
    <Modal
      className="file-preview-modal"
      alignCenter
      closable={false}
      title={file ? <div className="file-preview-heading">
        <span className="file-preview-name" title={file.originalName}>预览：{file.originalName}</span>
        <div className="file-preview-controls" ref={setToolbar} />
        <Button className="file-preview-close" type="text" aria-label="关闭文件预览" icon={<IconClose />} onClick={onClose} />
      </div> : ''}
      visible={Boolean(file && kind)}
      onCancel={onClose}
      footer={null}
      style={{ display: 'inline-flex', width: 'calc(100vw - 24px)', maxWidth: 'none', height: 'calc(100dvh - 24px)' }}
      unmountOnExit
    >
      {file && kind && (
        <div className="file-preview-surface">
          <Suspense fallback={<div role="status">加载预览…</div>}>
            {kind === 'pdf'
              ? <PdfPreview fileId={file.id} toolbarContainer={toolbar}
                loadContent={async (signal) => (await api.previewBlob(file.id, signal)).arrayBuffer()} />
              : <OemImage file={file} />}
          </Suspense>
          <PreviewWatermark employeeNo={employeeNo} realName={realName} />
        </div>
      )}
    </Modal>
  )
}
