import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Message } from '@arco-design/web-react'

const mocks = vi.hoisted(() => ({
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  fileMd5: vi.fn(),
  uploadFingerprint: vi.fn(),
  blobSha256: vi.fn(),
  disposeHasher: vi.fn(),
  createChunkHasher: vi.fn(),
}))

vi.mock('../../../api/client', () => ({
  default: { post: mocks.post, put: mocks.put, delete: mocks.delete },
}))
vi.mock('../../../api/file-hash', () => ({
  fileMd5: mocks.fileMd5,
  uploadFingerprint: mocks.uploadFingerprint,
  // Chunk digests come from a per-upload Worker-backed hasher; the mock keeps one shared digest source.
  createChunkHasher: mocks.createChunkHasher,
}))

import ChunkUploader from '../../../components/ChunkUploader'
import { hasActiveUpload } from '../../../components/uploadActivity'

function uploadFile(name: string, size = 1) {
  return new File([new Uint8Array(size)], name, { type: 'application/octet-stream' })
}

function initResponse(name: string, overrides: Record<string, unknown> = {}) {
  return {
    data: {
      sessionId: name,
      chunkSize: 1,
      totalChunks: 1,
      uploadedChunks: [],
      ...overrides,
    },
  }
}

function installDefaultHttp() {
  mocks.post.mockImplementation((url: string, body?: { fileName?: string }) => {
    if (url === '/uploads/init') return Promise.resolve(initResponse(body?.fileName ?? 'session'))
    return Promise.resolve({ data: { id: 99 } })
  })
  mocks.put.mockResolvedValue({ data: {} })
  mocks.delete.mockResolvedValue({ data: {} })
  mocks.fileMd5.mockResolvedValue('digest')
  mocks.uploadFingerprint.mockResolvedValue('f'.repeat(64))
  mocks.blobSha256.mockResolvedValue('c'.repeat(64))
}

function renderUploader(_unused = false, direction?: 'C2S' | 'S2C') {
  const callbacks = {
    onClose: vi.fn<() => void>(),
    onDone: vi.fn<() => void>(),
    onAllUploaded: vi.fn<() => void>(),
  }
  const view = render(<ChunkUploader
    projectId={1}
    visible
    onClose={callbacks.onClose}
    onDone={callbacks.onDone}
    onAllUploaded={callbacks.onAllUploaded}
    direction={direction}
  />)
  return { ...view, callbacks }
}

function choose(...files: File[]) {
  fireEvent.change(screen.getByLabelText('选择上传文件'), { target: { files } })
}

function dropFiles(...files: File[]) {
  const zone = screen.getByText('或将文件拖到此处').closest('.upload-dialog-drop')
  if (!zone) throw new Error('找不到拖放区域')
  fireEvent.drop(zone, { dataTransfer: { files, types: ['Files'] } })
}

function startButton() {
  return screen.getByRole('button', { name: /^上传所选文件/ })
}

function closeButton() {
  return screen.getByRole('button', { name: '关闭' })
}

function sessionCalls(kind: 'init' | 'merge') {
  return mocks.post.mock.calls.filter(([url]) => kind === 'init' ? url === '/uploads/init' : String(url).endsWith('/merge'))
}

function abortedUpload(_url: string, _blob: Blob, options: { signal: AbortSignal }) {
  return new Promise((_resolve, reject) => {
    const abort = () => reject(Object.assign(new Error('aborted'), { code: 'ERR_CANCELED' }))
    if (options.signal.aborted) abort()
    else options.signal.addEventListener('abort', abort, { once: true })
  })
}

describe('ChunkUploader DOM contracts', () => {
  beforeEach(() => {
    mocks.post.mockReset()
    mocks.put.mockReset()
    mocks.delete.mockReset()
    mocks.fileMd5.mockReset()
    mocks.uploadFingerprint.mockReset()
    mocks.blobSha256.mockReset()
    mocks.disposeHasher.mockReset()
    mocks.createChunkHasher.mockReset()
    mocks.createChunkHasher.mockImplementation(() => ({ sha256: mocks.blobSha256, dispose: mocks.disposeHasher }))
    installDefaultHttp()
  })

  it('adds files dropped onto the upload dialog', () => {
    renderUploader()
    dropFiles(uploadFile('拖入.xlsx'), uploadFile('assembly.stp'))
    expect(screen.getByText(/拖入\.xlsx/)).toBeVisible()
    expect(screen.getByText(/assembly\.stp/)).toBeVisible()
    expect(startButton()).toBeEnabled()
  })

  it('creates hash workers only after a file receives one of the two upload slots', async () => {
    mocks.put.mockImplementation(abortedUpload)
    const view = renderUploader()
    choose(uploadFile('a'), uploadFile('b'), uploadFile('c'))
    fireEvent.click(startButton())

    await waitFor(() => expect(mocks.createChunkHasher).toHaveBeenCalledTimes(2))
    expect(sessionCalls('init')).toHaveLength(2)
    view.unmount()
  })

  it('keeps the leave guard while hashing and releases it when the uploader unmounts', async () => {
    mocks.uploadFingerprint.mockReturnValue(new Promise<string>(() => undefined))
    const view = renderUploader()
    expect(hasActiveUpload()).toBe(false)
    choose(uploadFile('hold.bin'))
    fireEvent.click(startButton())
    await waitFor(() => expect(hasActiveUpload()).toBe(true))
    view.unmount()
    expect(hasActiveUpload()).toBe(false)
  })

  it('reports whole-file hash progress while upload fingerprinting is pending', async () => {
    let releaseFingerprint!: (value: string) => void
    mocks.uploadFingerprint.mockReturnValue(new Promise<string>((resolve) => { releaseFingerprint = resolve }))
    mocks.fileMd5.mockImplementation(async (_file, _cancelled, onProgress: (fraction: number) => void) => {
      onProgress(0.42)
      return 'digest'
    })
    renderUploader()
    choose(uploadFile('progress.bin'))
    fireEvent.click(startButton())

    expect(await screen.findByText('正在校验文件内容 42%')).toBeVisible()
    releaseFingerprint('f'.repeat(64))
    await screen.findByText('已完成')
  })

  it('limits chunk requests across files to four active requests', async () => {
    let active = 0
    let maximum = 0
    let hold = true
    const releases: Array<() => void> = []
    mocks.post.mockImplementation((url: string, body?: { fileName?: string }) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse(body?.fileName ?? 'session', { totalChunks: 3 }))
      return Promise.resolve({ data: { id: 99 } })
    })
    mocks.put.mockImplementation(() => {
      active += 1
      maximum = Math.max(maximum, active)
      if (!hold) {
        active -= 1
        return Promise.resolve({ data: {} })
      }
      return new Promise((resolve) => releases.push(() => { active -= 1; resolve({ data: {} }) }))
    })
    const { callbacks } = renderUploader()
    choose(uploadFile('a', 3), uploadFile('b', 3))
    fireEvent.click(startButton())

    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(4))
    expect(maximum).toBe(4)
    hold = false
    releases.splice(0).forEach(release => release())
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(6))
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledTimes(2))
    expect(maximum).toBe(4)
  })

  it('failed cancellation keeps the dialog and server session until a successful retry', async () => {
    mocks.put.mockImplementation(abortedUpload)
    mocks.delete.mockRejectedValueOnce(new Error('network unavailable')).mockResolvedValueOnce({ data: {} })
    const { callbacks } = renderUploader()
    choose(uploadFile('a'))
    fireEvent.click(startButton())
    await waitFor(() => expect(mocks.put).toHaveBeenCalledOnce())

    fireEvent.click(closeButton())
    expect(await screen.findByRole('button', { name: '重试取消' })).toBeInTheDocument()
    expect(callbacks.onClose).not.toHaveBeenCalled()
    expect(callbacks.onAllUploaded).not.toHaveBeenCalled()

    fireEvent.click(closeButton())
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
    expect(mocks.delete).toHaveBeenCalledTimes(2)
    expect(callbacks.onAllUploaded).not.toHaveBeenCalled()
  })

  it('removing an interrupted upload cleans its server session and retains retry on cleanup failure', async () => {
    mocks.put.mockRejectedValue(new Error('chunk failed'))
    mocks.delete.mockRejectedValueOnce(new Error('network')).mockResolvedValueOnce({ data: {} })
    const { callbacks } = renderUploader()
    choose(uploadFile('a'))
    fireEvent.click(startButton())
    await screen.findByText(/上传中断/)

    fireEvent.click(screen.getByRole('button', { name: '移除「a」' }))
    expect(await screen.findByRole('button', { name: '重试取消' })).toBeInTheDocument()
    expect(mocks.delete).toHaveBeenNthCalledWith(1, '/uploads/a')
    expect(callbacks.onClose).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: '重试取消' }))
    await screen.findByText('已取消')
    expect(screen.queryByRole('button', { name: '重试取消' })).not.toBeInTheDocument()
    expect(mocks.delete).toHaveBeenNthCalledWith(2, '/uploads/a')
    expect(callbacks.onAllUploaded).not.toHaveBeenCalled()
    fireEvent.click(closeButton())
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
  })

  it('one completed file plus one cancelled file never reports batch success', async () => {
    mocks.put.mockImplementation((url: string, blob: Blob, options: { signal: AbortSignal }) =>
      url.includes('/a/') ? Promise.resolve({ data: {} }) : abortedUpload(url, blob, options))
    const { callbacks } = renderUploader(true)
    choose(uploadFile('a'), uploadFile('b'))
    fireEvent.click(startButton())
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())

    fireEvent.click(await screen.findByRole('button', { name: '取消' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledWith('/uploads/b'))
    expect(callbacks.onAllUploaded).not.toHaveBeenCalled()
    expect(callbacks.onClose).not.toHaveBeenCalled()
  })

  it('removing a failed file cannot turn a partial batch into a successful delivery', async () => {
    mocks.put.mockImplementation((url: string) => url.includes('/b/')
      ? Promise.reject(new Error('chunk failed'))
      : Promise.resolve({ data: {} }))
    const { callbacks } = renderUploader(true)
    choose(uploadFile('a'), uploadFile('b'))
    fireEvent.click(startButton())
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    await screen.findByText(/上传中断/)

    fireEvent.click(screen.getByRole('button', { name: '移除「b」' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledWith('/uploads/b'))
    expect(callbacks.onAllUploaded).not.toHaveBeenCalled()
  })

  it('closing an uncertain merge does not abort the session', async () => {
    mocks.post.mockImplementation((url: string, body?: { fileName?: string }) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse(body?.fileName ?? 'a'))
      if (url.endsWith('/md5')) return Promise.resolve({ data: {} })
      return Promise.reject(new Error('response lost'))
    })
    const { callbacks } = renderUploader(true)
    choose(uploadFile('a'))
    fireEvent.click(startButton())
    await screen.findByRole('button', { name: '重试确认' })

    fireEvent.click(closeButton())
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
    expect(mocks.delete).not.toHaveBeenCalled()
    expect(callbacks.onAllUploaded).not.toHaveBeenCalled()
  })

  it('a fully successful batch reports success once and closes the dialog', async () => {
    let releaseSecond!: () => void
    const secondChunk = new Promise<void>((resolve) => { releaseSecond = resolve })
    mocks.put.mockImplementation((url: string) => url.includes('/b/') ? secondChunk : Promise.resolve({ data: {} }))
    const { callbacks } = renderUploader()
    choose(uploadFile('a'), uploadFile('b'))
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
    fireEvent.click(startButton())

    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    await act(async () => releaseSecond())
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
    expect(callbacks.onDone).toHaveBeenCalledTimes(2)
    expect(callbacks.onAllUploaded).toHaveBeenCalledOnce()
  })

  it('a supplier signature is sent with every upload init', async () => {
    mocks.put.mockResolvedValue({ data: {} })
    render(<ChunkUploader projectId={1} visible onClose={() => undefined} onDone={() => undefined} requireSignature />)
    const signature = screen.getByRole('textbox', { name: '签名' })
    expect(signature).toHaveValue('')
    fireEvent.change(signature, { target: { value: ' 张三 ' } })
    choose(uploadFile('a'))
    fireEvent.click(startButton())
    await waitFor(() => expect(mocks.post).toHaveBeenCalledWith('/uploads/init',
      expect.objectContaining({ fileName: 'a', signerName: '张三' }), expect.anything()))
  })

  it('duplicate close and start requests cannot overlap pending cancellation', async () => {
    mocks.put.mockImplementation(abortedUpload)
    let releaseDelete!: () => void
    mocks.delete.mockReturnValue(new Promise<void>((resolve) => { releaseDelete = resolve }))
    const { callbacks } = renderUploader()
    choose(uploadFile('a'))
    const start = startButton()
    act(() => {
      start.click()
      start.click()
    })
    await waitFor(() => expect(mocks.put).toHaveBeenCalledOnce())

    const close = closeButton()
    act(() => {
      close.click()
      close.click()
    })
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledOnce())
    expect(callbacks.onClose).not.toHaveBeenCalled()
    expect(screen.getByLabelText('选择上传文件')).toBeDisabled()
    expect(sessionCalls('init')).toHaveLength(1)

    await act(async () => releaseDelete())
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
  })

  it('a transient chunk failure is retried quietly and the upload completes without user action', async () => {
    const quiet: boolean[] = []
    mocks.put.mockImplementation((_url: string, _blob: Blob, config: { quietNetworkError: boolean }) => {
      quiet.push(config.quietNetworkError)
      if (quiet.length === 1) return Promise.reject(Object.assign(new Error('network'), { code: 'ERR_NETWORK' }))
      return Promise.resolve({ data: {} })
    })
    const { callbacks } = renderUploader()
    choose(uploadFile('a'))
    fireEvent.click(startButton())

    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce(), { timeout: 4000 })
    expect(quiet).toEqual([true, true])
    expect(sessionCalls('merge').map(([url]) => url)).toEqual(['/uploads/a/merge'])
    expect(mocks.delete).not.toHaveBeenCalled()
  }, 6000)

  it('chunk retries stop after the attempt limit and only the last failure is shown', async () => {
    const quiet: boolean[] = []
    mocks.put.mockImplementation((_url: string, _blob: Blob, config: { quietNetworkError: boolean }) => {
      quiet.push(config.quietNetworkError)
      return Promise.reject(Object.assign(new Error('network'), { code: 'ERR_NETWORK' }))
    })
    renderUploader()
    choose(uploadFile('a'))
    fireEvent.click(startButton())

    await screen.findByText(/上传中断/, {}, { timeout: 5000 })
    expect(quiet).toEqual([true, true, false])
    expect(sessionCalls('merge')).toHaveLength(0)
  }, 7000)

  it('business rejections of a chunk are not retried', async () => {
    mocks.put.mockRejectedValue(Object.assign(new Error('conflict'), { response: { status: 409 } }))
    renderUploader()
    choose(uploadFile('a'))
    fireEvent.click(startButton())

    await screen.findByText(/上传中断/)
    expect(mocks.put).toHaveBeenCalledOnce()
    expect(sessionCalls('merge')).toHaveLength(0)
  })

  it('at most two files upload at once and a waiting file can be removed before it starts', async () => {
    const releases = new Map<string, () => void>()
    mocks.put.mockImplementation((url: string) => new Promise<void>((resolve) => {
      releases.set(url.split('/')[2], resolve)
    }))
    renderUploader()
    choose(uploadFile('a'), uploadFile('b'), uploadFile('c'), uploadFile('d'))
    fireEvent.click(startButton())
    await waitFor(() => expect(sessionCalls('init').map(([, body]) => body.fileName).sort()).toEqual(['a', 'b']))
    expect(screen.getAllByText(/排队中/)).toHaveLength(2)

    fireEvent.click(screen.getByRole('button', { name: '移除「d」' }))
    await waitFor(() => expect(screen.queryByRole('button', { name: '移除「d」' })).not.toBeInTheDocument())
    await act(async () => releases.get('a')?.())
    await waitFor(() => expect(sessionCalls('init').map(([, body]) => body.fileName).sort()).toEqual(['a', 'b', 'c']))
    await act(async () => {
      releases.get('b')?.()
      releases.get('c')?.()
    })
    await waitFor(() => expect(sessionCalls('merge').map(([url]) => url).sort()).toEqual([
      '/uploads/a/merge', '/uploads/b/merge', '/uploads/c/merge',
    ]))
    expect(sessionCalls('init').some(([, body]) => body.fileName === 'd')).toBe(false)
  })

  it('starts chunk upload before the full MD5 is ready, then submits it before merge', async () => {
    let releaseMd5!: (digest: string) => void
    mocks.fileMd5.mockReturnValue(new Promise<string>((resolve) => { releaseMd5 = resolve }))
    const { callbacks } = renderUploader()
    const file = uploadFile('parallel.pdf')
    choose(file)
    fireEvent.click(startButton())

    await waitFor(() => expect(mocks.put).toHaveBeenCalledOnce())
    expect(mocks.post.mock.calls.some(([url]) => String(url).endsWith('/md5'))).toBe(false)
    expect(sessionCalls('merge')).toHaveLength(0)
    const initBody = sessionCalls('init')[0][1]
    expect(initBody).toMatchObject({
      fileName: 'parallel.pdf',
      fileSize: file.size,
      fileLastModified: file.lastModified,
      fileFingerprint: 'f'.repeat(64),
    })
    expect(initBody).not.toHaveProperty('fileMd5')

    await act(async () => releaseMd5('browser-full-md5'))
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    const uploadPosts = mocks.post.mock.calls.map(([url]) => String(url))
    expect(uploadPosts).toEqual([
      '/uploads/init',
      '/uploads/parallel.pdf/md5',
      '/uploads/parallel.pdf/merge',
    ])
  })

  it('revalidates resumed chunks against the current file digest and replaces a mismatch', async () => {
    mocks.post.mockImplementation((url: string) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse('same-fingerprint', {
        uploadedChunks: [{ index: 0, sha256: 'a'.repeat(64) }],
      }))
      return Promise.resolve({ data: { id: 99 } })
    })
    mocks.blobSha256.mockResolvedValue('b'.repeat(64))
    const { callbacks } = renderUploader()
    choose(uploadFile('same-edge-bytes.pdf'))
    fireEvent.click(startButton())

    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    expect(mocks.disposeHasher).toHaveBeenCalledOnce()
    expect(mocks.put).toHaveBeenCalledOnce()
    expect(mocks.put.mock.calls[0][2]).toMatchObject({
      headers: { 'X-Chunk-SHA256': 'b'.repeat(64) },
    })
  })

  it('skips a resumed chunk only when its server digest matches the current file', async () => {
    mocks.post.mockImplementation((url: string) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse('resumed', {
        uploadedChunks: [{ index: 0, sha256: 'c'.repeat(64) }],
      }))
      return Promise.resolve({ data: { id: 99 } })
    })
    const { callbacks } = renderUploader()
    choose(uploadFile('resume.pdf'))
    fireEvent.click(startButton())

    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    expect(mocks.put).not.toHaveBeenCalled()
    expect(mocks.post.mock.calls.some(([url]) => String(url).endsWith('/md5'))).toBe(true)
    expect(sessionCalls('merge')).toHaveLength(1)
  })

  it('does not merge when the parallel full-file hash fails', async () => {
    mocks.fileMd5.mockRejectedValue(new Error('worker hash failed'))
    renderUploader()
    choose(uploadFile('hash-failed.pdf'))
    fireEvent.click(startButton())

    await screen.findByText(/上传中断/)
    expect(mocks.put).toHaveBeenCalledOnce()
    expect(mocks.post.mock.calls.some(([url]) => String(url).endsWith('/md5'))).toBe(false)
    expect(sessionCalls('merge')).toHaveLength(0)
  })

  it('lost upload merge response retries only merge on the same session', async () => {
    let mergeCalls = 0
    mocks.post.mockImplementation((url: string, body?: { fileName?: string }) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse(body?.fileName ?? 'same-session', { sessionId: 'same-session' }))
      if (url.endsWith('/md5')) return Promise.resolve({ data: {} })
      mergeCalls += 1
      return mergeCalls === 1 ? Promise.reject(new Error('response lost after commit')) : Promise.resolve({ data: { id: 99 } })
    })
    const { callbacks } = renderUploader()
    choose(uploadFile('sample.pdf'))
    fireEvent.click(startButton())
    fireEvent.click(await screen.findByRole('button', { name: '重试确认' }))

    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
    expect(sessionCalls('init')).toHaveLength(1)
    expect(mocks.put).toHaveBeenCalledOnce()
    expect(sessionCalls('merge')).toHaveLength(2)
    expect(mocks.post.mock.calls.filter(([url]) => String(url).endsWith('/merge'))
      .every(([, , config]) => (config as { timeout?: number } | undefined)?.timeout === 600000)).toBe(true)
    expect(mocks.delete).not.toHaveBeenCalled()
    expect(callbacks.onDone).toHaveBeenCalledOnce()
  })

  it('a definitive upload integrity failure can discard the damaged session and retry cleanup', async () => {
    let initCalls = 0
    let deleteFailures = 1
    mocks.post.mockImplementation((url: string) => {
      if (url === '/uploads/init') {
        initCalls += 1
        return Promise.resolve(initResponse('', { sessionId: initCalls === 1 ? 'damaged-session' : 'fresh-session' }))
      }
      if (url === '/uploads/damaged-session/merge') {
        return Promise.reject({ response: { status: 400, data: { message: '文件 MD5 校验失败，请重新上传' } } })
      }
      if (url.endsWith('/md5')) return Promise.resolve({ data: {} })
      return Promise.resolve({ data: { id: 99 } })
    })
    mocks.delete.mockImplementation(() => deleteFailures-- > 0
      ? Promise.reject(new Error('temporary cleanup failure'))
      : Promise.resolve({ data: {} }))
    const { callbacks } = renderUploader()
    const file = uploadFile('damaged.pdf')
    choose(file)
    fireEvent.click(startButton())
    const cleanup = await screen.findByRole('button', { name: '清理并移除' })

    fireEvent.click(cleanup)
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledTimes(1))
    expect(screen.getByRole('button', { name: '清理并移除' })).toBeInTheDocument()
    expect(screen.getAllByText(/damaged.pdf/).length).toBeGreaterThan(0)

    fireEvent.click(screen.getByRole('button', { name: '清理并移除' }))
    await waitFor(() => expect(screen.queryByRole('button', { name: '清理并移除' })).not.toBeInTheDocument())
    expect(screen.getByLabelText('选择上传文件')).not.toBeDisabled()
    choose(file)
    fireEvent.click(startButton())
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
    expect(sessionCalls('merge').map(([url]) => url)).toEqual([
      '/uploads/damaged-session/merge', '/uploads/fresh-session/merge',
    ])
    expect(initCalls).toBe(2)
    expect(callbacks.onDone).toHaveBeenCalledOnce()
  })

  it('concurrent merge confirmation clicks issue only one retry request', async () => {
    let mergeCalls = 0
    let releaseRetry!: (value: unknown) => void
    const retry = new Promise((resolve) => { releaseRetry = resolve })
    mocks.post.mockImplementation((url: string) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse('', { sessionId: 'same-session' }))
      if (url.endsWith('/md5')) return Promise.resolve({ data: {} })
      mergeCalls += 1
      if (mergeCalls === 1) return Promise.reject(new Error('response lost after commit'))
      return retry
    })
    const { callbacks } = renderUploader()
    choose(uploadFile('sample.pdf'))
    fireEvent.click(startButton())
    const button = await screen.findByRole('button', { name: '重试确认' })

    act(() => {
      button.click()
      button.click()
    })
    expect(mergeCalls).toBe(2)
    await act(async () => releaseRetry({ data: { id: 99 } }))
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
    expect(sessionCalls('init')).toHaveLength(1)
    expect(mocks.put).toHaveBeenCalledOnce()
    expect(callbacks.onDone).toHaveBeenCalledOnce()
  })

  it('a failed upload aborts sibling chunks promptly and resumes the same session', async () => {
    let initCalls = 0
    let siblingAborted = false
    const puts: Array<{ attempt: number; chunk: number }> = []
    mocks.post.mockImplementation((url: string) => {
      if (url.endsWith('/merge')) return Promise.resolve({ data: { id: 99 } })
      if (url.endsWith('/md5')) return Promise.resolve({ data: {} })
      initCalls += 1
      return Promise.resolve(initResponse('', {
        sessionId: 'test-session',
        totalChunks: 3,
        uploadedChunks: initCalls === 1 ? [] : [{ index: 0, sha256: 'c'.repeat(64) }],
      }))
    })
    mocks.put.mockImplementation((url: string, _blob: Blob, config: { signal: AbortSignal }) => {
      const chunk = Number(url.split('/').at(-1))
      puts.push({ attempt: initCalls, chunk })
      if (initCalls > 1 || chunk === 0) return Promise.resolve({ data: {} })
      if (chunk === 1) return Promise.reject(new Error('original chunk failure'))
      return new Promise((_resolve, reject) => {
        const abort = () => {
          siblingAborted = true
          reject(Object.assign(new Error('sibling aborted'), { name: 'AbortError' }))
        }
        if (config.signal.aborted) abort()
        else config.signal.addEventListener('abort', abort, { once: true })
      })
    })
    const { callbacks } = renderUploader()
    choose(uploadFile('sample.pdf', 3))
    fireEvent.click(startButton())
    await screen.findByText(/上传中断/)

    expect(siblingAborted).toBe(true)
    expect(puts.filter((item) => item.attempt === 1).map((item) => item.chunk).sort()).toEqual([0, 1, 2])
    expect(sessionCalls('merge')).toHaveLength(0)
    fireEvent.click(startButton())
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    expect(initCalls).toBe(2)
    expect(puts.filter((item) => item.attempt === 2).map((item) => item.chunk).sort()).toEqual([1, 2])
    expect(sessionCalls('merge')).toHaveLength(1)
  })

  it('closing waits for old chunks to settle before another upload can start', async () => {
    let releaseOld!: () => void
    let releaseNew!: () => void
    const oldChunk = new Promise<void>((resolve) => { releaseOld = resolve })
    const newChunk = new Promise<void>((resolve) => { releaseNew = resolve })
    let inits = 0
    const events: string[] = []
    mocks.post.mockImplementation((url: string) => {
      if (url.endsWith('/merge')) {
        events.push(url)
        return Promise.resolve({ data: {} })
      }
      if (url.endsWith('/md5')) return Promise.resolve({ data: {} })
      inits += 1
      return Promise.resolve(initResponse('', { sessionId: `session-${inits}` }))
    })
    mocks.put.mockImplementation((url: string) => url.includes('session-1') ? oldChunk : newChunk)
    const { callbacks } = renderUploader()
    choose(uploadFile('sample.pdf'))
    fireEvent.click(startButton())
    await waitFor(() => expect(mocks.put).toHaveBeenCalledOnce())
    fireEvent.click(closeButton())
    expect(callbacks.onClose).not.toHaveBeenCalled()

    await act(async () => releaseOld())
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
    choose(uploadFile('sample.pdf'))
    fireEvent.click(startButton())
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2))
    expect(events).toEqual([])
    expect(callbacks.onDone).not.toHaveBeenCalled()

    await act(async () => releaseNew())
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    expect(events).toEqual(['/uploads/session-2/merge'])
  })

  it('merging upload cannot be reported cancelled while its file commits', async () => {
    let releaseMerge!: (value: unknown) => void
    const merge = new Promise((resolve) => { releaseMerge = resolve })
    mocks.post.mockImplementation((url: string) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse('', {
        sessionId: 'session-1',
        uploadedChunks: [{ index: 0, sha256: 'c'.repeat(64) }],
      }))
      if (url.endsWith('/md5')) return Promise.resolve({ data: {} })
      return merge
    })
    const { callbacks } = renderUploader()
    choose(uploadFile('sample.pdf'))
    fireEvent.click(startButton())
    await screen.findByRole('button', { name: '处理中，请稍候' })

    expect(screen.queryByRole('button', { name: '关闭' })).not.toBeInTheDocument()
    expect(mocks.delete).not.toHaveBeenCalled()
    await act(async () => releaseMerge({ data: { id: 1 } }))
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    expect(callbacks.onClose).toHaveBeenCalledOnce()
  })

  it('late initialization settles and cancels before reopening can reuse its session', async () => {
    let releaseInit!: (value: unknown) => void
    const lateInit = new Promise((resolve) => { releaseInit = resolve })
    let initCount = 0
    const events: string[] = []
    mocks.post.mockImplementation((url: string) => {
      if (url.endsWith('/merge')) {
        events.push('merge')
        return Promise.resolve({ data: { id: 1 } })
      }
      if (url.endsWith('/md5')) return Promise.resolve({ data: {} })
      events.push('init')
      initCount += 1
      return initCount === 1
        ? lateInit
        : Promise.resolve(initResponse('', {
          sessionId: 'shared-session',
          uploadedChunks: [{ index: 0, sha256: 'c'.repeat(64) }],
        }))
    })
    mocks.delete.mockImplementation(() => {
      events.push('delete')
      return Promise.resolve({ data: {} })
    })
    const { callbacks } = renderUploader()
    choose(uploadFile('sample.pdf'))
    fireEvent.click(startButton())
    await waitFor(() => expect(sessionCalls('init')).toHaveLength(1))
    fireEvent.click(closeButton())
    expect(callbacks.onClose).not.toHaveBeenCalled()

    await act(async () => releaseInit(initResponse('', { sessionId: 'shared-session' })))
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
    choose(uploadFile('sample.pdf'))
    fireEvent.click(startButton())
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    expect(events).toEqual(['init', 'delete', 'init', 'merge'])
  })
})

describe('ChunkUploader upload material rules', () => {
  beforeEach(() => {
    mocks.post.mockReset()
    mocks.put.mockReset()
    mocks.delete.mockReset()
    mocks.createChunkHasher.mockReset()
    mocks.createChunkHasher.mockImplementation(() => ({ sha256: mocks.blobSha256, dispose: mocks.disposeHasher }))
    installDefaultHttp()
  })

  it('initializes same-batch Excel and STEP/archive files before other company-to-supplier files', async () => {
    let releaseArchive!: () => void
    mocks.post.mockImplementation((url: string, body?: { fileName?: string }) => {
      if (url !== '/uploads/init') return Promise.resolve({ data: { id: 99 } })
      const response = initResponse(body?.fileName ?? 'session')
      if (body?.fileName !== '模型.7z') return Promise.resolve(response)
      return new Promise((resolve) => { releaseArchive = () => resolve(response) })
    })
    renderUploader(false, 'C2S')
    choose(uploadFile('说明.pdf'), uploadFile('动作流程.xlsx'), uploadFile('模型.7z'))
    fireEvent.click(startButton())

    expect(await screen.findByText('等待同批 Excel 与 STEP/压缩文件开始上传')).toBeVisible()
    await waitFor(() => expect(sessionCalls('init')).toHaveLength(2))
    expect(sessionCalls('init').map(([, body]) => (body as { fileName: string }).fileName).sort())
      .toEqual(['动作流程.xlsx', '模型.7z'].sort())
    releaseArchive()
    await waitFor(() => expect(sessionCalls('merge')).toHaveLength(3))
    expect(sessionCalls('init')[2][1]).toMatchObject({ fileName: '说明.pdf' })
  })

  it('merges same-batch company files only after the Excel and STEP/archive files have been merged', async () => {
    let releaseStepMerge!: () => void
    mocks.post.mockImplementation((url: string, body?: { fileName?: string }) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse(body?.fileName ?? 'session'))
      if (String(url).includes('assembly.step') && String(url).endsWith('/merge')) {
        return new Promise((resolve) => { releaseStepMerge = () => resolve({ data: { id: 98 } }) })
      }
      return Promise.resolve({ data: { id: 99 } })
    })
    renderUploader(false, 'C2S')
    choose(uploadFile('说明.pdf'), uploadFile('assembly.step'), uploadFile('动作流程.xlsm'))
    fireEvent.click(startButton())

    expect(await screen.findByText('等待同批 Excel 与 STEP/压缩文件上传完成')).toBeVisible()
    await waitFor(() => expect(sessionCalls('merge')).toHaveLength(2))
    expect(sessionCalls('merge').map(([url]) => String(url)).some((url) => url.includes('说明.pdf'))).toBe(false)
    releaseStepMerge()
    await waitFor(() => expect(sessionCalls('merge')).toHaveLength(3))
    expect(String(sessionCalls('merge')[2][0])).toContain('说明.pdf')
  })

  it('blocks a company batch until it has both an Excel file and a STEP or archive file', () => {
    renderUploader(false, 'C2S')
    expect(screen.getByText('发给供应商的资料要求')).toBeVisible()
    const examples = screen.getByRole('group', { name: '资料要求示例' })
    expect(Array.from(examples.querySelectorAll('img')).map((image) => image.getAttribute('alt')))
      .toEqual(['A. STEP 格式 3D 总装图示例', 'B. 动作流程说明 Excel 示例'])

    choose(uploadFile('动作流程.xlsx'))
    expect(startButton()).toBeDisabled()
    expect(screen.getByText(/本批需要同时包含动作流程 Excel/)).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: '移除「动作流程.xlsx」' }))

    choose(uploadFile('assembly.stp'), uploadFile('说明.pdf'))
    expect(startButton()).toBeDisabled()
    expect(screen.getByText(/本批需要同时包含动作流程 Excel/)).toBeVisible()

    choose(uploadFile('CSLR-605 放板機 2105931-1 動作流程.xlsx'))
    expect(startButton()).toBeEnabled()
    expect(screen.queryByText(/本批需要同时包含动作流程 Excel/)).toBeNull()
  })

  it('accepts an archive in place of STEP files for a company batch', () => {
    renderUploader(false, 'C2S')
    choose(uploadFile('CSU-424R 模擬.7z'), uploadFile('CSU-424R 模擬資料.xlsx'))

    expect(startButton()).toBeEnabled()
    expect(screen.queryByText(/本批需要同时包含动作流程 Excel/)).toBeNull()
  })

  it('shows supplier examples without restricting supplier-to-company files', async () => {
    renderUploader(false, 'S2C')
    const examples = screen.getByRole('group', { name: '上传资料示例' })
    expect(examples.querySelectorAll('img')).toHaveLength(2)
    choose(uploadFile('说明.pdf'))

    expect(startButton()).toBeEnabled()
    expect(screen.queryByText(/本批需要同时包含动作流程 Excel/)).toBeNull()
    fireEvent.click(startButton())
    await screen.findByText('已完成')
  })

  it('business rejections at init become a terminal state with one aggregated toast', async () => {
    const materialMessage = '发给供应商的资料需要同时包含动作流程 Excel 和 STEP 格式 3D 图（.step/.stp）或压缩文件（.zip/.rar/.7z），请与本批文件一起上传'
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    mocks.post.mockImplementation((url: string) => {
      if (url === '/uploads/init') return Promise.reject({ response: { status: 400, data: { code: 40001, message: materialMessage } } })
      return Promise.resolve({ data: { id: 99 } })
    })
    renderUploader()
    choose(uploadFile('说明.pdf'), uploadFile('图纸.pdf'))
    fireEvent.click(startButton())

    await waitFor(() => expect(screen.getAllByText(materialMessage)).toHaveLength(2))
    expect(sessionCalls('init')).toHaveLength(2)
    expect(sessionCalls('init').every(([, , config]) => (config as { quietClientError?: boolean }).quietClientError)).toBe(true)
    expect(toast.mock.calls.filter(([message]) => message === materialMessage)).toHaveLength(1)
    expect(within(screen.getByRole('dialog')).queryByText(/上传中断/)).toBeNull()
    expect(screen.getAllByText('服务端拒绝，未上传')).toHaveLength(2)
    expect(startButton()).toBeDisabled()
    expect(mocks.put).not.toHaveBeenCalled()
    const row = screen.getByRole('group', { name: '说明.pdf' })
    const error = row.querySelector('.upload-entry-error')!
    expect(error.id).toBeTruthy()
    expect(row).toHaveAttribute('aria-describedby', error.id)
    expect(error.parentElement).toHaveAttribute('aria-live', 'polite')
    toast.mockRestore()
  })

  it('a merge-time business rejection is terminal instead of awaiting confirmation', async () => {
    const materialMessage = '发给供应商的资料需要同时包含动作流程 Excel 和 STEP 格式 3D 图（.step/.stp）或压缩文件（.zip/.rar/.7z），请与本批文件一起上传'
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    mocks.post.mockImplementation((url: string, body?: { fileName?: string }) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse(body?.fileName ?? 'session'))
      if (String(url).endsWith('/merge')) return Promise.reject({ response: { status: 400, data: { message: materialMessage } } })
      return Promise.resolve({ data: {} })
    })
    const { callbacks } = renderUploader()
    choose(uploadFile('说明.pdf'))
    fireEvent.click(startButton())

    expect(await screen.findByText(materialMessage)).toBeVisible()
    expect(sessionCalls('merge')).toHaveLength(1)
    expect(screen.queryByRole('button', { name: '重试确认' })).toBeNull()
    const row = screen.getByRole('group', { name: '说明.pdf' })
    expect(within(row).getByText('服务端拒绝，未上传')).toBeVisible()
    expect(within(row).queryByText(/上传中断|结果待确认/)).toBeNull()
    expect(toast).toHaveBeenCalledTimes(1)
    expect(callbacks.onDone).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: '移除「说明.pdf」' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledWith('/uploads/说明.pdf'))
    toast.mockRestore()
  })
})
