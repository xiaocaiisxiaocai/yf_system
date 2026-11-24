import { useRef, useState } from 'react'
import { Button, Empty, Popover, Space, Spin, Tag } from '@arco-design/web-react'
import type { OutsourceItemFilesResponse } from '../../api/generated/api-types'
import { requestErrorMessage } from '../../api/requestError'
import { fmtSize, fmtTime } from '../../api/types'
import LoadError from '../../components/LoadError'
import OemFilePreview, { type OemPreviewFile } from '../../oem/components/OemFilePreview'
import { downloadOemFile } from '../../oem/components/downloadOemFile'
import { OEM_PREVIEW_MAX_BYTES, oemPreviewKind } from '../../oem/components/oemPreview'
import { useOem } from '../../oem/OemContext'
import { extensionOf } from '../../utils/uploadExtensions'

type FilesLoad = { status: 'loading' } | { status: 'error'; message: string } | { status: 'ready'; response: OutsourceItemFilesResponse }

const directions: Record<string, string> = { INTERNAL_TO_OEM: '发给厂商', OEM_TO_INTERNAL: '厂商发来' }
const states: Record<string, { label: string; color: string }> = {
  AVAILABLE: { label: '可用', color: 'green' },
  PROCESSING: { label: '处理中', color: 'orange' },
  PURGED: { label: '已清除', color: 'gray' },
}

export default function ItemFilesPopover({ fileCount, purgedFileCount, loadFiles }: {
  fileCount: number
  purgedFileCount: number
  loadFiles: () => Promise<OutsourceItemFilesResponse>
}) {
  const { api } = useOem()
  const [visible, setVisible] = useState(false)
  const [load, setLoad] = useState<FilesLoad>({ status: 'loading' })
  const [preview, setPreview] = useState<OemPreviewFile | null>(null)
  const request = useRef(0)
  const hasFiles = fileCount > 0
  const purgedOnly = !hasFiles && purgedFileCount > 0
  const label = hasFiles ? `${fileCount} 个文件` : purgedOnly ? '已清除' : '无文件'

  const fetchFiles = async () => {
    const sequence = ++request.current
    setLoad({ status: 'loading' })
    try {
      const response = await loadFiles()
      if (sequence === request.current) setLoad({ status: 'ready', response })
    } catch (error) {
      if (sequence === request.current) setLoad({ status: 'error', message: requestErrorMessage(error, '档案清单加载失败') })
    }
  }

  const changeVisibility = (next: boolean) => {
    setVisible(next)
    if (next) void fetchFiles()
    else request.current += 1
  }

  return <>
    <Popover trigger="click" position="bottom" title="档案清单" popupVisible={visible} onVisibleChange={changeVisibility}
      triggerProps={{ escToClose: true }} style={{ maxWidth: 'none' }} content={<div className="outsource-files-popover">
        {load.status === 'loading' && <div role="status" aria-label="正在加载档案"><Spin /></div>}
        {load.status === 'error' && <LoadError message={load.message} onRetry={() => void fetchFiles()} />}
        {load.status === 'ready' && (load.response.files.length === 0 ? <Empty description="暂无档案" />
          : <ul className="outsource-files-list">
            {load.response.files.map((file) => {
              const state = states[file.state]
              const ext = extensionOf(file.name)
              return <li key={file.fileId} className="outsource-files-row">
                <div className="outsource-files-summary">
                  <span className={file.state === 'PURGED' ? 'outsource-files-name--purged' : undefined}>{file.name}</span>
                  <Tag size="small">{directions[file.direction] ?? file.direction}</Tag>
                  <Tag size="small" color={state?.color}>{state?.label ?? file.state}</Tag>
                </div>
                <div className="outsource-files-details">
                  <span>{fmtSize(file.sizeBytes)}</span><span>{file.vendorName}</span><span>{fmtTime(file.sentAt)}</span>
                  {load.response.canOpen === true && file.state === 'AVAILABLE' && <Space size={4} className="outsource-files-actions">
                    <Button type="text" size="mini" aria-label={`预览 ${file.name}`}
                      disabled={!oemPreviewKind(ext) || file.sizeBytes > OEM_PREVIEW_MAX_BYTES}
                      onClick={() => setPreview({ id: file.fileId, originalName: file.name, ext })}>预览</Button>
                    <Button type="text" size="mini" aria-label={`下载 ${file.name}`}
                      onClick={() => void downloadOemFile(api, file.fileId).catch(() => undefined)}>下载</Button>
                  </Space>}
                </div>
              </li>
            })}
          </ul>)}
      </div>}>
      <Button type="text" size="mini" aria-label={`档案：${label}`} aria-expanded={visible}>
        <span aria-hidden className={hasFiles ? 'outsource-files-light outsource-files-light--available' : 'outsource-files-light'} />
        {purgedOnly ? '已清除' : fileCount}
      </Button>
    </Popover>
    {preview && <OemFilePreview file={preview} onClose={() => setPreview(null)} />}
  </>
}
