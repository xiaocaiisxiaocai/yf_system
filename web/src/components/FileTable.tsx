import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Tooltip, Typography,
} from '@arco-design/web-react'
import { IconDownload, IconEye, IconUpload, IconDelete } from '@arco-design/web-react/icon'
import http, { type QuietRequestConfig } from '../api/client'
import { useAuth } from '../store/auth'
import { type FileItem, type PageResp, fmtSize, fmtTime } from '../api/types'
import { actionSlots } from './ActionSlots'
import ChunkUploader from './ChunkUploader'
import PdfPreview from './PdfPreview'
import { useCollaboration } from '../store/collaboration'

// Excel 解析器仅在用户真正打开工作簿预览时按需加载
const ExcelPreview = lazy(() => import('./ExcelPreview'))

interface Props {
  projectId: number
  projectStatus: string
  targetId?: number
}

const PDF_PREVIEW_MAX_BYTES = 50 * 1024 * 1024

/** 依据扩展名判定预览能力：Excel→只读表格，PDF→内联，其余仅下载 */
function previewKind(ext: string, sizeBytes = 0): 'excel' | 'pdf' | 'none' {
  // Excel 需要在浏览器内完整解析工作簿，限制在线预览体积以避免页面 OOM。
  if ((ext === 'xlsx' || ext === 'xls') && sizeBytes <= 50 * 1024 * 1024) return 'excel'
  // PDF.js 同样会在浏览器内完整缓冲文件；较大文件仅允许下载。
  if (ext === 'pdf' && sizeBytes <= PDF_PREVIEW_MAX_BYTES) return 'pdf'
  return 'none'
}

/** 认证下载：拉 blob 后触发浏览器保存（直接拼 URL 会丢 Authorization 头） */
async function downloadAuthed(id: number, name: string) {
  const r = await http.get(`/files/${id}/download`, { responseType: 'blob' })
  const url = URL.createObjectURL(r.data as Blob)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  a.click()
  URL.revokeObjectURL(url)
}

export default function FileTable({ projectId, projectStatus, targetId }: Props) {
  const [previewToolbar, setPreviewToolbar] = useState<HTMLDivElement | null>(null)
  const revision = useCollaboration((state) => state.revision)
  const syncStatus = useCollaboration((state) => state.status)
  const [data, setData] = useState<PageResp<FileItem>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [reloadKey, setReloadKey] = useState(0)
  const [direction, setDirection] = useState<string>()
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [loadError, setLoadError] = useState(false)
  const [uploadOpen, setUploadOpen] = useState(false)
  const [preview, setPreview] = useState<FileItem | null>(null)
  const [selected, setSelected] = useState<number[]>([])
  const [batchDownloading, setBatchDownloading] = useState(false)
  const { hasPerm } = useAuth()

  // 递增序号防止并发加载乱序：快速切换筛选时只允许最后一次请求落地
  const loadSeq = useRef(0)
  const batchDownloadInFlight = useRef(false)

  const fetchFiles = useCallback(async () => {
    const r = await http.get(`/projects/${projectId}/files`, {
      params: { page, pageSize, direction, keyword: keyword || undefined, targetId },
      quietNetworkError: true,
    } as QuietRequestConfig)
    return r.data as PageResp<FileItem>
  }, [projectId, page, pageSize, direction, keyword, targetId])

  const load = useCallback(() => {
    setLoading(true)
    setSelected([])
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    if (syncStatus === 'error') return
    const seq = ++loadSeq.current
    let active = true
    fetchFiles()
      .then((next) => {
        if (active && seq === loadSeq.current) {
          setData(next)
          setSelected((current) => current.filter((id) => next.list.some((file) => file.id === id)))
          setLoadError(false)
          const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || pageSize)))
          if (page > lastPage) setPage(lastPage)
        }
      })
      .catch(() => {
        if (active && seq === loadSeq.current) setLoadError(true)
      })
      .finally(() => {
        if (active && seq === loadSeq.current) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [fetchFiles, page, pageSize, reloadKey, revision, syncStatus])

  useEffect(() => {
    // The upload dialog is owned by this component and must close when the project becomes read-only.
    // eslint-disable-next-line react/set-state-in-effect
    if (projectStatus !== 'IN_PROGRESS') setUploadOpen(false)
  }, [projectStatus])

  const startUpload = () => {
    if (projectStatus !== 'IN_PROGRESS') return
    setUploadOpen(true)
  }

  const batchDownload = async () => {
    if (batchDownloadInFlight.current) return
    batchDownloadInFlight.current = true
    setBatchDownloading(true)
    try {
      const r = await http.post('/files/batch-download', { ids: selected }, { responseType: 'blob' })
      const url = URL.createObjectURL(r.data as Blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `项目文件打包_${Date.now()}.zip`
      a.click()
      URL.revokeObjectURL(url)
      setSelected([])
    } finally {
      batchDownloadInFlight.current = false
      setBatchDownloading(false)
    }
  }

  const remove = async (f: FileItem) => {
    await http.delete(`/files/${f.id}`)
    Message.success('已删除')
    load()
  }

  return (
    <div>
      <Space className="responsive-toolbar" style={{ marginBottom: 12, width: '100%', justifyContent: 'space-between' }}>
        <Space>
          <Select
            allowClear
            placeholder="方向"
            style={{ width: 150 }}
            onChange={(v) => {
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(1)
              setDirection(v as string | undefined)
            }}
          >
            <Select.Option value="C2S">公司 → 供应商</Select.Option>
            <Select.Option value="S2C">供应商 → 公司</Select.Option>
          </Select>
          <Input.Search
            allowClear
            placeholder="文件名"
            style={{ width: 200 }}
            onSearch={(v) => {
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(1)
              setKeyword(v)
            }}
            onClear={() => {
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(1)
              setKeyword('')
            }}
          />
        </Space>
        <Space>
          {selected.length > 0 && hasPerm('file:download') && (
            <Button icon={<IconDownload />} onClick={batchDownload} disabled={loading || batchDownloading} loading={batchDownloading}>
              打包下载（{selected.length}）
            </Button>
          )}
          {!targetId && hasPerm('file:upload') && projectStatus === 'IN_PROGRESS' && (
            <Button type="primary" icon={<IconUpload />} onClick={startUpload}>
              上传文件
            </Button>
          )}
        </Space>
      </Space>

      {loadError ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={load}>重试</Button>
        </div>
      ) : (
        <Table
          rowKey="id"
          loading={loading}
          data={data.list}
          rowSelection={hasPerm('file:download') ? {
            selectedRowKeys: selected,
            onChange: (keys) => setSelected(keys as number[]),
          } : undefined}
          scroll={{ x: 960 }}
          columns={[
          {
            title: '文件名',
            dataIndex: 'originalName',
            width: 200,
            ellipsis: true,
            render: (v: string, r: FileItem) => (
              <Space size={4} className="file-name-cell">
                <span className="table-cell-text" title={v}>{v}</span>
                {previewKind(r.ext, r.sizeBytes) !== 'none' && hasPerm('file:preview') && (
                  <Tooltip content="在线预览">
                    <Button size="mini" type="text" icon={<IconEye />} aria-label="预览文件" onClick={() => setPreview(r)} />
                  </Tooltip>
                )}
              </Space>
            ),
          },
          {
            title: '方向',
            dataIndex: 'direction',
            width: 130,
            align: 'center' as const,
            render: (v: string) =>
              v === 'C2S' ? <Tag color="arcoblue">公司 → 供应商</Tag> : <Tag color="purple">供应商 → 公司</Tag>,
          },
          { title: '大小', dataIndex: 'sizeBytes', width: 90, align: 'center' as const, render: fmtSize },
          { title: '上传人', dataIndex: 'uploaderName', width: 90, align: 'center' as const, ellipsis: true },
          { title: '上传时间', dataIndex: 'createdAt', width: 180, align: 'center' as const, render: fmtTime },
          {
            title: '操作',
            width: 140,
            fixed: 'right' as const,
            align: 'center' as const,
            render: (_: unknown, r: FileItem) => actionSlots([
              hasPerm('file:download') && (
                <Button key="download" size="mini" type="text" icon={<IconDownload />} aria-label="下载文件" onClick={() => downloadAuthed(r.id, r.originalName)}>
                  下载
                </Button>
              ),
              r.canDelete && projectStatus === 'IN_PROGRESS' && (
                <Popconfirm key="delete" title={`删除文件「${r.originalName}」？`} onOk={() => remove(r)}>
                  <Button size="mini" type="text" status="danger" icon={<IconDelete />} aria-label="删除文件">删除</Button>
                </Popconfirm>
              ),
            ], 'file'),
          },
          ]}
          pagination={{
            total: data.total,
            current: page,
            pageSize,
            showTotal: true,
            onChange: (p, ps) => {
              setLoading(true); setLoadError(false); setReloadKey((value) => value + 1)
              setPage(p)
              setPageSize(ps)
            },
          }}
        />
      )}

      {uploadOpen && (
        <ChunkUploader
          projectId={projectId}
          visible={uploadOpen}
          onClose={() => setUploadOpen(false)}
          onDone={load}
        />
      )}
      <Modal
        className="file-preview-modal"
        alignCenter
        title={preview ? <div className="file-preview-heading">
          <span className="file-preview-name" title={preview.originalName}>预览：{preview.originalName}</span>
          <div className="file-preview-controls" ref={setPreviewToolbar} />
        </div> : ''}
        visible={!!preview}
        onCancel={() => setPreview(null)}
        footer={null}
        style={{ display: 'inline-flex', width: 'calc(100vw - 24px)', maxWidth: 'none', height: 'calc(100dvh - 24px)' }}
        unmountOnExit
      >
        {preview && previewKind(preview.ext, preview.sizeBytes) === 'excel' && (
          <Suspense fallback={<div style={{ textAlign: 'center', padding: 60 }}>加载 Excel 渲染器…</div>}>
            <ExcelPreview fileId={preview.id} />
          </Suspense>
        )}
        {preview && previewKind(preview.ext, preview.sizeBytes) === 'pdf' && <PdfPreview fileId={preview.id} toolbarContainer={previewToolbar} />}
      </Modal>
    </div>
  )
}
