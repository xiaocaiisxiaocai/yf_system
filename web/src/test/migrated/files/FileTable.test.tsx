import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
  downloadFile: vi.fn(),
  downloadFiles: vi.fn(),
  permissions: new Set<string>(),
  user: { id: 1, userType: 'INTERNAL', employeeNo: 'E-007', realName: '测试用户' },
  revision: '',
  status: 'ready',
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get, post: mocks.post, delete: mocks.delete },
}))
vi.mock('../../../api/download', () => ({
  downloadFile: mocks.downloadFile,
  downloadFiles: mocks.downloadFiles,
}))
vi.mock('../../../store/auth', () => ({
  useAuth: () => ({ user: mocks.user, hasPerm: (permission: string) => mocks.permissions.has(permission) }),
}))
vi.mock('../../../store/collaboration', () => ({
  useCollaboration: (selector: (state: { revision: string; status: string }) => unknown) => selector({
    revision: mocks.revision,
    status: mocks.status,
  }),
}))
vi.mock('../../../components/ChunkUploader', () => ({ default: () => <div data-testid="chunk-uploader" /> }))
vi.mock('../../../components/PdfPreview', () => ({ default: () => <div data-testid="pdf-preview" /> }))
vi.mock('../../../components/ExcelPreview', () => ({ default: () => <div data-testid="excel-preview" /> }))
vi.mock('../../../components/PptxPreview', () => ({ default: () => <div data-testid="pptx-preview" /> }))
vi.mock('../../../components/VideoPreview', () => ({ default: () => <div data-testid="video-preview" /> }))
vi.mock('../../../components/ImagePreview', () => ({ default: () => <div data-testid="image-preview" /> }))

import FileTable from '../../../components/FileTable'
import { MessageImages } from '../../../components/MessageImages'

type FileRow = {
  id: number
  originalName: string
  ext: string
  sizeBytes: number
  direction: 'C2S' | 'S2C'
  uploaderName: string
  createdAt: string
  canDelete: boolean
  isCopiedReference?: boolean
}

function row(id: number, name: string, ext: string, sizeBytes: number, extra: Partial<FileRow> = {}): FileRow {
  return {
    id,
    originalName: name,
    ext,
    sizeBytes,
    direction: 'C2S',
    uploaderName: '上传人',
    createdAt: '2026-09-23T00:00:00Z',
    canDelete: false,
    ...extra,
  }
}

function response(list: FileRow[]) {
  return { data: { list, total: list.length, page: 1, pageSize: 50 } }
}

async function renderTable(list: FileRow[], props: Partial<React.ComponentProps<typeof FileTable>> = {}) {
  mocks.get.mockResolvedValue(response(list))
  const view = render(<FileTable projectId={1} projectStatus="IN_PROGRESS" {...props} />)
  if (list.length) await screen.findByText(list[0].originalName)
  else await waitFor(() => expect(mocks.get).toHaveBeenCalled())
  return view
}

function tableRow(name: string) {
  const cell = screen.getByText(name)
  const tr = cell.closest('tr')
  if (!tr) throw new Error(`table row not found for ${name}`)
  return within(tr)
}

describe('FileTable DOM contracts', () => {
  beforeEach(() => {
    mocks.permissions.clear()
    mocks.get.mockReset()
    mocks.post.mockReset()
    mocks.delete.mockReset()
    mocks.downloadFile.mockReset()
    mocks.downloadFiles.mockReset()
    mocks.revision = ''
    mocks.status = 'ready'
  })

  it('preview dispatch offers PPTX within the parser limit and streamed videos independently of document size', async () => {
    mocks.permissions.add('file:preview')
    const limit = 50 * 1024 * 1024
    const supported = [
      row(1, 'inside.PPTX', 'PPTX', limit),
      row(3, 'movie.mp4', 'mp4', 1024 * 1024 * 1024),
      row(4, 'movie.webm', 'webm', 1024 * 1024 * 1024),
      row(5, 'movie.ogv', 'ogv', 1024 * 1024 * 1024),
      row(6, 'inside.png', 'png', limit),
      row(8, 'inside.JPEG', 'JPEG', limit),
    ]
    const unsupported = [
      row(2, 'outside.pptx', 'pptx', limit + 1),
      row(7, 'outside.png', 'png', limit + 1),
      row(9, 'unsafe.svg', 'svg', 1024),
      row(10, 'legacy.ppt', 'ppt', 1024),
      row(11, 'page.html', 'html', 1024),
      row(12, 'legacy.avi', 'avi', 1024),
      row(13, 'legacy.mkv', 'mkv', 1024),
    ]
    const firstView = await renderTable(supported)

    for (const name of ['inside.PPTX', 'movie.mp4', 'movie.webm', 'movie.ogv', 'inside.png', 'inside.JPEG']) {
      expect(tableRow(name).getByRole('button', { name: '预览文件' })).toBeInTheDocument()
    }
    firstView.unmount()
    await renderTable(unsupported)
    for (const name of ['outside.pptx', 'outside.png', 'unsafe.svg', 'legacy.ppt', 'page.html', 'legacy.avi', 'legacy.mkv']) {
      expect(tableRow(name).queryByRole('button', { name: '预览文件' })).not.toBeInTheDocument()
    }
  })

  it('PDF preview accepts the 50 MiB boundary and leaves larger files download-only', async () => {
    mocks.permissions.add('file:preview')
    const limit = 50 * 1024 * 1024
    await renderTable([
      row(1, 'boundary.pdf', 'pdf', limit),
      row(2, 'too-large.pdf', 'pdf', limit + 1),
    ])

    expect(tableRow('boundary.pdf').getByRole('button', { name: '预览文件' })).toBeInTheDocument()
    expect(tableRow('too-large.pdf').queryByRole('button', { name: '预览文件' })).not.toBeInTheDocument()
  })

  it('copied project files expose a compact copy-history action without changing preview behavior', async () => {
    mocks.permissions.add('file:preview')
    const openHistory = vi.fn()
    await renderTable([
      row(7, '引用文件.pdf', 'pdf', 100, { isCopiedReference: true }),
      row(8, '普通文件.pdf', 'pdf', 100),
    ], { projectStatus: 'DRAFT', onOpenCopyHistory: openHistory })

    const copied = tableRow('引用文件.pdf')
    await userEvent.click(copied.getByRole('button', { name: '查看文件「引用文件.pdf」的复制履历' }))
    expect(openHistory).toHaveBeenCalledOnce()
    expect(copied.getByRole('button', { name: '预览文件' })).toBeInTheDocument()
    expect(tableRow('普通文件.pdf').queryByRole('button', { name: /复制履历/ })).not.toBeInTheDocument()
  })

  it('file filtering clears a selection that is no longer visible', async () => {
    mocks.permissions.add('file:download')
    const initial = row(23, 'selected.pdf', 'pdf', 1)
    mocks.get
      .mockResolvedValueOnce(response([initial]))
      .mockResolvedValue(response([]))
    render(<FileTable projectId={1} projectStatus="IN_PROGRESS" />)
    await screen.findByText(initial.originalName)
    const rowCheckbox = tableRow(initial.originalName).getByRole('checkbox')
    await userEvent.click(rowCheckbox)
    expect(screen.getByRole('button', { name: '打包下载（1）' })).toBeInTheDocument()

    const search = screen.getByPlaceholderText('文件名')
    await userEvent.type(search, 'different-file')
    fireEvent.keyDown(search, { key: 'Enter', code: 'Enter', keyCode: 13, charCode: 13, which: 13 })

    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.queryByRole('button', { name: '打包下载（1）' })).not.toBeInTheDocument())
  })

  it('file preview, download and delete icon actions expose accessible names', async () => {
    mocks.permissions.add('file:preview')
    mocks.permissions.add('file:download')
    await renderTable([row(1, '图纸.pdf', 'pdf', 1, { canDelete: true })])
    const current = tableRow('图纸.pdf')

    expect(current.getByRole('button', { name: '预览文件' })).toBeInTheDocument()
    expect(current.getByRole('button', { name: '下载文件' })).toBeInTheDocument()
    expect(current.getByRole('button', { name: '删除文件' })).toBeInTheDocument()

    await userEvent.click(current.getByRole('button', { name: '预览文件' }))
    expect(await screen.findByTestId('pdf-preview')).toBeInTheDocument()
    expect(document.querySelector('.file-preview-modal .arco-modal-footer')).not.toBeInTheDocument()
  })

  it('handles a failed file deletion without removing the row and permits retry', async () => {
    mocks.delete.mockRejectedValueOnce(new Error('delete failed')).mockResolvedValueOnce({ data: {} })
    await renderTable([row(1, '保留.pdf', 'pdf', 1, { canDelete: true })])

    await userEvent.click(tableRow('保留.pdf').getByRole('button', { name: '删除文件' }))
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledTimes(1))
    expect(screen.getByText('保留.pdf')).toBeVisible()

    await userEvent.click(tableRow('保留.pdf').getByRole('button', { name: '删除文件' }))
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledTimes(2))
  })

  it('all file preview branches and message image preview mount the existing watermark layer', async () => {
    mocks.permissions.add('file:preview')
    const previews = [
      row(1, 'sheet.xlsx', 'xlsx', 1),
      row(2, 'document.pdf', 'pdf', 1),
      row(3, 'picture.png', 'png', 1),
      row(4, 'slides.pptx', 'pptx', 1),
      row(5, 'movie.mp4', 'mp4', 1024),
    ]

    for (const file of previews) {
      mocks.get.mockResolvedValue(response([file]))
      const view = render(<FileTable projectId={1} projectStatus="IN_PROGRESS" />)
      await screen.findByText(file.originalName)
      await userEvent.click(tableRow(file.originalName).getByRole('button', { name: '预览文件' }))
      await waitFor(() => expect(document.querySelector('.preview-watermark')).toBeInTheDocument())
      expect(document.querySelector('.preview-watermark')).toHaveTextContent('E-007 测试用户')
      view.unmount()
    }

    const originalCreateObjectURL = URL.createObjectURL
    const originalRevokeObjectURL = URL.revokeObjectURL
    URL.createObjectURL = vi.fn(() => 'blob:message-image')
    URL.revokeObjectURL = vi.fn()
    mocks.get.mockResolvedValue({ data: new Blob(['image']) })
    const callsBeforeMessageImage = mocks.get.mock.calls.length
    render(<MessageImages
      messageId={5}
      images={[{ id: 9, name: '留言.png', sizeBytes: 5, mimeType: 'image/png' }]}
      watermarkEmployeeNo="E-008"
      watermarkRealName="留言用户"
    />)
    const image = await screen.findByAltText('留言.png')
    fireEvent.load(image)
    await userEvent.click(screen.getByRole('button', { name: '预览图片：留言.png' }))
    await waitFor(() => expect(document.querySelector('.preview-watermark')).toBeInTheDocument())
    expect(document.querySelector('.preview-watermark')).toHaveTextContent('E-008 留言用户')
    expect(mocks.get).toHaveBeenCalledTimes(callsBeforeMessageImage + 1)
    URL.createObjectURL = originalCreateObjectURL
    URL.revokeObjectURL = originalRevokeObjectURL
  })

  it('batch download uses a synchronous in-flight latch against repeated clicks', async () => {
    mocks.permissions.add('file:download')
    let release!: () => void
    mocks.downloadFiles.mockReturnValue(new Promise<void>((resolve) => { release = resolve }))
    await renderTable([row(7, 'a.pdf', 'pdf', 1)])
    await userEvent.click(tableRow('a.pdf').getByRole('checkbox'))
    const button = screen.getByRole('button', { name: '打包下载（1）' })

    act(() => {
      button.click()
      button.click()
    })
    expect(mocks.downloadFiles).toHaveBeenCalledTimes(1)
    expect(mocks.downloadFiles).toHaveBeenCalledWith([7])
    await act(async () => release())
  })

  it('file download actions delegate to the native authenticated download helper', async () => {
    mocks.permissions.add('file:download')
    mocks.downloadFile.mockResolvedValue(undefined)
    await renderTable([row(7, '图纸.pdf', 'pdf', 1)])

    await userEvent.click(tableRow('图纸.pdf').getByRole('button', { name: '下载文件' }))

    expect(mocks.downloadFile).toHaveBeenCalledWith(7)
  })
})
