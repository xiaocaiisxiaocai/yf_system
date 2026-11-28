import { Suspense, lazy, useCallback, useEffect, useState, type ReactNode } from 'react'
import { Button, Modal, Result, Spin } from '@arco-design/web-react'
import { IconClose } from '@arco-design/web-react/icon'
import { PreviewWatermark } from '../../components/PreviewWatermark'
import PreviewFallback from '../../components/PreviewFallback'
import { createPreviewDeadline } from '../../components/previewDeadline'
import { useAuth } from '../../store/auth'
import { usePortalAuth } from '../api/portalSession'
import type { Realm, TransferFile } from '../api/types'
import { useOem } from '../OemContext'
import { oemPreviewKind } from './oemPreview'

export type OemPreviewFile = Pick<TransferFile, 'id' | 'originalName' | 'ext'>
type LoadBlob = (id: number, signal: AbortSignal, onProgress?: (loaded: number) => void) => Promise<Blob>

const PdfPreview = lazy(() => import('../../components/PdfPreview'))
const ImagePreview = lazy(() => import('../../components/ImagePreview'))
const ExcelPreview = lazy(() => import('../../components/ExcelPreview'))
const PptxPreview = lazy(() => import('../../components/PptxPreview'))
const DocxPreview = lazy(() => import('../../components/DocxPreview'))

/** Watermark identity of whoever is looking: the internal user or the OEM vendor account. */
function useViewerIdentity(realm: Realm): { employeeNo?: string; realName?: string } {
  const internalUser = useAuth((state) => state.user)
  const portalAccount = usePortalAuth((state) => state.account)
  return realm === 'oem'
    ? { employeeNo: portalAccount?.employeeNo }
    : { employeeNo: internalUser?.employeeNo, realName: internalUser?.realName }
}

function PreviewImage({ file, toolbarContainer, loadBlob }: {
  file: OemPreviewFile; toolbarContainer: HTMLElement | null; loadBlob: LoadBlob
}) {
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<{ url?: string; failed?: boolean }>({})
  const retry = useCallback(() => {
    setState({})
    setAttempt((value) => value + 1)
  }, [])
  useEffect(() => {
    let active = true
    const controller = new AbortController()
    let url = ''
    const deadline = createPreviewDeadline(() => {
      active = false
      controller.abort()
      setState({ failed: true })
    })
    loadBlob(file.id, controller.signal, (loaded) => deadline.progress(loaded)).then((blob) => {
      if (!active || controller.signal.aborted) return
      deadline.clear()
      url = URL.createObjectURL(blob)
      setState({ url })
    }).catch(() => {
      deadline.clear()
      if (active && !controller.signal.aborted) setState({ failed: true })
    })
    return () => {
      active = false
      deadline.clear()
      controller.abort()
      if (url) URL.revokeObjectURL(url)
    }
  }, [loadBlob, file.id, attempt])
  if (state.failed) {
    return <Result status="error" title="图片预览失败" subTitle="读取失败，请重试。"
      extra={<Button onClick={retry}>重试图片预览</Button>} />
  }
  if (!state.url) return <div role="status" style={{ textAlign: 'center', padding: 60 }}><Spin /> 正在加载图片…</div>
  return <ImagePreview sourceUrl={state.url} name={file.originalName} toolbarContainer={toolbarContainer} onRetry={retry} />
}

/**
 * In-page OEM attachment preview with the same watermark (employee no. + name + open time)
 * and modal layout as the collaboration file preview. Bytes come from the OEM content endpoint.
 */
export default function OemFilePreview({ file, onClose }: { file: OemPreviewFile | null; onClose: () => void }) {
  const { api, realm } = useOem()
  const loadBlob = useCallback<LoadBlob>((id, signal, onProgress) =>
    onProgress ? api.previewBlob(id, signal, onProgress) : api.previewBlob(id, signal), [api])
  return <FileBlobPreview file={file} realm={realm} loadBlob={loadBlob} onClose={onClose} />
}

export function FileBlobPreview({ file, realm, loadBlob, navigation, onClose }: {
  file: OemPreviewFile | null; realm: Realm; loadBlob: LoadBlob; navigation?: ReactNode; onClose: () => void
}) {
  const { employeeNo, realName } = useViewerIdentity(realm)
  const [toolbar, setToolbar] = useState<HTMLDivElement | null>(null)
  const kind = file ? oemPreviewKind(file.ext) : null
  const fileId = file?.id
  const loadContent = useCallback(
    async (signal: AbortSignal, onProgress?: (loaded: number) => void) =>
      (await loadBlob(fileId!, signal, onProgress)).arrayBuffer(),
    [loadBlob, fileId],
  )
  return (
    <Modal
      className="file-preview-modal"
      alignCenter
      closable={false}
      title={file ? <div className="file-preview-heading">
        <span className="file-preview-name" title={file.originalName}>预览：{file.originalName}</span>
        {navigation}
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
          <Suspense fallback={<PreviewFallback />}>
            {kind === 'pdf' && <PdfPreview fileId={file.id} toolbarContainer={toolbar} loadContent={loadContent} />}
            {kind === 'image' && <PreviewImage key={file.id} file={file} toolbarContainer={toolbar} loadBlob={loadBlob} />}
            {kind === 'excel' && <ExcelPreview fileId={file.id} ext={file.ext} loadContent={loadContent} />}
            {kind === 'pptx' && <PptxPreview fileId={file.id} toolbarContainer={toolbar} loadContent={loadContent} />}
            {kind === 'docx' && <DocxPreview fileId={file.id} toolbarContainer={toolbar} loadContent={loadContent} />}
          </Suspense>
          <PreviewWatermark key={file.id} employeeNo={employeeNo} realName={realName} />
        </div>
      )}
    </Modal>
  )
}
