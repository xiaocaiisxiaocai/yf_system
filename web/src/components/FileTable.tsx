import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Button, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Tooltip,
} from '@arco-design/web-react'
import { IconDownload, IconEye, IconUpload, IconDelete } from '@arco-design/web-react/icon'
import http from '../api/client'
import { useAuth } from '../store/auth'
import { type FileItem, type PageResp, type Round, fmtSize, fmtTime } from '../api/types'
import ChunkUploader from './ChunkUploader'
import PdfPreview from './PdfPreview'

// Excel 解析器仅在用户真正打开工作簿预览时按需加载
const ExcelPreview = lazy(() => import('./ExcelPreview'))

interface Props {
  projectId: number
  projectStatus: string
  rounds: Round[]
}

/** 依据扩展名判定预览能力：Excel→只读表格，PDF→内联，其余仅下载 */
function previewKind(ext: string, sizeBytes = 0): 'excel' | 'pdf' | 'none' {
  // Excel 需要在浏览器内完整解析工作簿，限制在线预览体积以避免页面 OOM。
  if ((ext === 'xlsx' || ext === 'xls') && sizeBytes <= 50 * 1024 * 1024) return 'excel'
  if (ext === 'pdf') return 'pdf'
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

export default function FileTable({ projectId, projectStatus, rounds }: Props) {
  const [data, setData] = useState<PageResp<FileItem>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [reloadKey, setReloadKey] = useState(0)
  const [roundId, setRoundId] = useState<number>()
  const [direction, setDirection] = useState<string>()
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [uploadOpen, setUploadOpen] = useState(false)
  const [uploadRound, setUploadRound] = useState<number>()
  const [preview, setPreview] = useState<FileItem | null>(null)
  const [selected, setSelected] = useState<number[]>([])
  const { hasPerm } = useAuth()

  const pendingRounds = useMemo(() => rounds.filter((r) => r.status === 'PENDING'), [rounds])
  const roundMap = useMemo(() => new Map(rounds.map((r) => [r.id, r.roundNo])), [rounds])

  // 递增序号防止并发加载乱序：快速切换筛选时只允许最后一次请求落地
  const loadSeq = useRef(0)

  const fetchFiles = useCallback(async () => {
    const r = await http.get(`/projects/${projectId}/files`, {
      params: { page, pageSize, roundId, direction, keyword: keyword || undefined },
    })
    return r.data as PageResp<FileItem>
  }, [projectId, page, pageSize, roundId, direction, keyword])

  const load = useCallback(async () => {
    const seq = ++loadSeq.current
    setLoading(true)
    setSelected([])
    try {
      const next = await fetchFiles()
      if (seq === loadSeq.current) setData(next)
    } finally {
      if (seq === loadSeq.current) setLoading(false)
    }
  }, [fetchFiles])

  useEffect(() => {
    const seq = ++loadSeq.current
    let active = true
    fetchFiles()
      .then((next) => {
        if (active && seq === loadSeq.current) {
          setData(next)
          setSelected([])
        }
      })
      .finally(() => {
        if (active && seq === loadSeq.current) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [fetchFiles, reloadKey])

  const startUpload = () => {
    if (pendingRounds.length === 0) {
      Message.warning('当前没有待确认的轮次，请先发起新一轮')
      return
    }
    setUploadRound(pendingRounds[pendingRounds.length - 1].id)
    setUploadOpen(true)
  }

  const roundOptions = pendingRounds.map((r) => ({ id: r.id, roundNo: r.roundNo }))

  const batchDownload = async () => {
    const r = await http.post('/files/batch-download', { ids: selected }, { responseType: 'blob' })
    const url = URL.createObjectURL(r.data as Blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `项目文件打包_${Date.now()}.zip`
    a.click()
    URL.revokeObjectURL(url)
    setSelected([])
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
            placeholder="轮次"
            style={{ width: 130 }}
            onChange={(v) => {
              setLoading(true); setReloadKey((value) => value + 1)
              setPage(1)
              setRoundId(v as number | undefined)
            }}
          >
            {rounds.map((r) => (
              <Select.Option key={r.id} value={r.id}>
                第 {r.roundNo} 轮
              </Select.Option>
            ))}
          </Select>
          <Select
            allowClear
            placeholder="方向"
            style={{ width: 150 }}
            onChange={(v) => {
              setLoading(true); setReloadKey((value) => value + 1)
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
              setLoading(true); setReloadKey((value) => value + 1)
              setPage(1)
              setKeyword(v)
            }}
            onClear={() => {
              setLoading(true); setReloadKey((value) => value + 1)
              setPage(1)
              setKeyword('')
            }}
          />
        </Space>
        <Space>
          {selected.length > 0 && hasPerm('file:download') && (
            <Button icon={<IconDownload />} onClick={batchDownload} disabled={loading}>
              打包下载（{selected.length}）
            </Button>
          )}
          {hasPerm('file:upload') && projectStatus === 'IN_PROGRESS' && (
            <Button type="primary" icon={<IconUpload />} onClick={startUpload}>
              上传文件
            </Button>
          )}
        </Space>
      </Space>

      <Table
        rowKey="id"
        loading={loading}
        data={data.list}
        rowSelection={hasPerm('file:download') ? {
          selectedRowKeys: selected,
          onChange: (keys) => setSelected(keys as number[]),
        } : undefined}
        scroll={{ x: 1000 }}
        columns={[
          {
            title: '文件名',
            dataIndex: 'originalName',
            width: 220,
            ellipsis: true,
            render: (v: string, r: FileItem) => (
              <Space size={4}>
                <span>{v}</span>
                {previewKind(r.ext, r.sizeBytes) !== 'none' && hasPerm('file:preview') && (
                  <Tooltip content="在线预览">
                    <Button size="mini" type="text" icon={<IconEye />} onClick={() => setPreview(r)} />
                  </Tooltip>
                )}
              </Space>
            ),
          },
          { title: '轮次', width: 70, align: 'center' as const, render: (_: unknown, r: FileItem) => `第 ${r.roundNo ?? roundMap.get(r.roundId) ?? '-'} 轮` },
          {
            title: '方向',
            dataIndex: 'direction',
            width: 140,
            align: 'center' as const,
            render: (v: string) =>
              v === 'C2S' ? <Tag color="arcoblue">公司 → 供应商</Tag> : <Tag color="purple">供应商 → 公司</Tag>,
          },
          { title: '大小', dataIndex: 'sizeBytes', width: 90, align: 'center' as const, render: fmtSize },
          { title: '上传人', dataIndex: 'uploaderName', width: 100, align: 'center' as const, ellipsis: true },
          { title: '上传时间', dataIndex: 'createdAt', width: 180, align: 'center' as const, render: fmtTime },
          {
            title: '操作',
            width: 150,
            align: 'center' as const,
            render: (_: unknown, r: FileItem) => (
              <Space>
                {hasPerm('file:download') && (
                  <Button size="mini" type="text" icon={<IconDownload />} onClick={() => downloadAuthed(r.id, r.originalName)}>
                    下载
                  </Button>
                )}
                {r.canDelete && (
                  <Popconfirm title={`删除文件「${r.originalName}」？`} onOk={() => remove(r)}>
                    <Button size="mini" type="text" status="danger" icon={<IconDelete />} />
                  </Popconfirm>
                )}
              </Space>
            ),
          },
        ]}
        pagination={{
          total: data.total,
          current: page,
          pageSize,
          showTotal: true,
          onChange: (p, ps) => {
            setLoading(true); setReloadKey((value) => value + 1)
            setPage(p)
            setPageSize(ps)
          },
        }}
      />

      {uploadOpen && uploadRound && (
        <ChunkUploader
          projectId={projectId}
          roundId={uploadRound}
          rounds={roundOptions}
          onRoundChange={setUploadRound}
          visible={uploadOpen}
          onClose={() => setUploadOpen(false)}
          onDone={load}
        />
      )}
      <Modal
        title={preview ? `预览：${preview.originalName}` : ''}
        visible={!!preview}
        onCancel={() => setPreview(null)}
        footer={hasPerm('file:download') ? (
          <Button icon={<IconDownload />} onClick={() => preview && downloadAuthed(preview.id, preview.originalName)}>
            下载原文件
          </Button>
        ) : null}
        style={{ width: '86vw', maxWidth: 1200 }}
        unmountOnExit
      >
        {preview && previewKind(preview.ext, preview.sizeBytes) === 'excel' && (
          <Suspense fallback={<div style={{ textAlign: 'center', padding: 60 }}>加载 Excel 渲染器…</div>}>
            <ExcelPreview fileId={preview.id} />
          </Suspense>
        )}
        {preview && previewKind(preview.ext, preview.sizeBytes) === 'pdf' && <PdfPreview fileId={preview.id} />}
      </Modal>
    </div>
  )
}
