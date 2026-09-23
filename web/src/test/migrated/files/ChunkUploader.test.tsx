import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  fileMd5: vi.fn(),
}))

vi.mock('../../../api/client', () => ({
  default: { post: mocks.post, put: mocks.put, delete: mocks.delete },
}))
vi.mock('../../../api/file-hash', () => ({ fileMd5: mocks.fileMd5 }))

import ChunkUploader from '../../../components/ChunkUploader'

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
}

function renderUploader(offerSubmit = false) {
  const callbacks = {
    onClose: vi.fn<() => void>(),
    onDone: vi.fn<() => void>(),
    onAllUploaded: vi.fn<() => void>(),
    onSubmitForAcceptance: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
  }
  const view = render(<ChunkUploader
    projectId={1}
    visible
    onClose={callbacks.onClose}
    onDone={callbacks.onDone}
    onAllUploaded={callbacks.onAllUploaded}
    onSubmitForAcceptance={offerSubmit ? callbacks.onSubmitForAcceptance : undefined}
  />)
  return { ...view, callbacks }
}

function choose(...files: File[]) {
  fireEvent.change(screen.getByLabelText('选择上传文件'), { target: { files } })
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
    installDefaultHttp()
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

  it('one completed file plus one cancelled file never reports batch success or submits acceptance', async () => {
    mocks.put.mockImplementation((url: string, blob: Blob, options: { signal: AbortSignal }) =>
      url.includes('/a/') ? Promise.resolve({ data: {} }) : abortedUpload(url, blob, options))
    const { callbacks } = renderUploader(true)
    choose(uploadFile('a'), uploadFile('b'))
    await userEvent.click(screen.getByRole('checkbox', { name: '全部上传成功后提交验收' }))
    fireEvent.click(startButton())
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())

    fireEvent.click(await screen.findByRole('button', { name: '取消' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledWith('/uploads/b'))
    expect(callbacks.onAllUploaded).not.toHaveBeenCalled()
    expect(callbacks.onSubmitForAcceptance).not.toHaveBeenCalled()
    expect(callbacks.onClose).not.toHaveBeenCalled()
  })

  it('removing a failed file cannot turn a partial batch into a successful delivery', async () => {
    mocks.put.mockImplementation((url: string) => url.includes('/b/')
      ? Promise.reject(new Error('chunk failed'))
      : Promise.resolve({ data: {} }))
    const { callbacks } = renderUploader(true)
    choose(uploadFile('a'), uploadFile('b'))
    await userEvent.click(screen.getByRole('checkbox', { name: '全部上传成功后提交验收' }))
    fireEvent.click(startButton())
    await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
    await screen.findByText(/上传中断/)

    fireEvent.click(screen.getByRole('button', { name: '移除「b」' }))
    await waitFor(() => expect(mocks.delete).toHaveBeenCalledWith('/uploads/b'))
    expect(callbacks.onAllUploaded).not.toHaveBeenCalled()
    expect(callbacks.onSubmitForAcceptance).not.toHaveBeenCalled()
  })

  it('closing an uncertain merge neither aborts the session nor submits acceptance', async () => {
    mocks.post.mockImplementation((url: string, body?: { fileName?: string }) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse(body?.fileName ?? 'a'))
      return Promise.reject(new Error('response lost'))
    })
    const { callbacks } = renderUploader(true)
    choose(uploadFile('a'))
    await userEvent.click(screen.getByRole('checkbox', { name: '全部上传成功后提交验收' }))
    fireEvent.click(startButton())
    await screen.findByRole('button', { name: '重试确认' })

    fireEvent.click(closeButton())
    await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
    expect(mocks.delete).not.toHaveBeenCalled()
    expect(callbacks.onAllUploaded).not.toHaveBeenCalled()
    expect(callbacks.onSubmitForAcceptance).not.toHaveBeenCalled()
  })

  for (const optIn of [false, true]) {
    it(`a fully successful batch submits acceptance only with explicit opt-in (${optIn})`, async () => {
      let releaseSecond!: () => void
      const secondChunk = new Promise<void>((resolve) => { releaseSecond = resolve })
      mocks.put.mockImplementation((url: string) => url.includes('/b/') ? secondChunk : Promise.resolve({ data: {} }))
      const { callbacks } = renderUploader(true)
      choose(uploadFile('a'), uploadFile('b'))
      const checkbox = screen.getByRole('checkbox', { name: '全部上传成功后提交验收' })
      expect(checkbox).not.toBeChecked()
      if (optIn) await userEvent.click(checkbox)
      fireEvent.click(startButton())

      await waitFor(() => expect(callbacks.onDone).toHaveBeenCalledOnce())
      await act(async () => releaseSecond())
      await waitFor(() => expect(callbacks.onClose).toHaveBeenCalledOnce())
      expect(callbacks.onDone).toHaveBeenCalledTimes(2)
      expect(callbacks.onAllUploaded).toHaveBeenCalledOnce()
      expect(callbacks.onSubmitForAcceptance).toHaveBeenCalledTimes(optIn ? 1 : 0)
    })
  }

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

  it('lost upload merge response retries only merge on the same session', async () => {
    let mergeCalls = 0
    mocks.post.mockImplementation((url: string, body?: { fileName?: string }) => {
      if (url === '/uploads/init') return Promise.resolve(initResponse(body?.fileName ?? 'same-session', { sessionId: 'same-session' }))
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
      initCalls += 1
      return Promise.resolve(initResponse('', {
        sessionId: 'test-session',
        totalChunks: 3,
        uploadedChunks: initCalls === 1 ? [] : [0],
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
    mocks.post.mockImplementation((url: string) => url === '/uploads/init'
      ? Promise.resolve(initResponse('', { sessionId: 'session-1', uploadedChunks: [0] }))
      : merge)
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
      events.push('init')
      initCount += 1
      return initCount === 1
        ? lateInit
        : Promise.resolve(initResponse('', { sessionId: 'shared-session', uploadedChunks: [0] }))
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
