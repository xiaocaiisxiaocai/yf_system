import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AxiosError, type AxiosAdapter, type AxiosInstance, type InternalAxiosRequestConfig } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Message } from '@arco-design/web-react'

const hashMocks = vi.hoisted(() => ({ fileMd5: vi.fn() }))
vi.mock('../../api/file-hash', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../api/file-hash')>()),
  fileMd5: hashMocks.fileMd5,
}))
const pdfProps = vi.hoisted(() => ({ loadContent: undefined as undefined | ((signal: AbortSignal) => Promise<ArrayBuffer>) }))
vi.mock('../../components/PdfPreview', () => ({
  default: (props: { loadContent?: (signal: AbortSignal) => Promise<ArrayBuffer> }) => {
    pdfProps.loadContent = props.loadContent
    return <div data-testid="pdf-preview" />
  },
}))

import { OemApi } from '../../oem/api/OemApi'
import { PORTAL_AUTH_LOCK_NAME, PORTAL_STORAGE_LOCK_KEY, withPortalStorageLease } from '../../oem/api/portalAuthLock'
import { bootPortalSession, portalHttp, usePortalAuth, type PortalAccount } from '../../oem/api/portalSession'
import type { TransferFile } from '../../oem/api/types'
import OemFilePreview from '../../oem/components/OemFilePreview'
import OemUploader from '../../oem/components/OemUploader'
import { OemProvider } from '../../oem/OemContext'

const ABC_SHA256 = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'

const account: PortalAccount = {
  id: 31, employeeNo: 'OEM31', realName: '厂商用户', email: 'oem31@example.test', companyId: 9, companyName: '测试代工厂',
}

function context(api: Partial<OemApi>, realm: 'internal' | 'oem' = 'internal') {
  return { api: api as OemApi, realm, base: realm === 'oem' ? '/oem-portal' : '/oem', permissions: new Set<string>(), userId: 7, queryScope: ['test'] }
}

function uploadApi(overrides: Partial<OemApi> = {}): Partial<OemApi> {
  return {
    initUpload: vi.fn().mockResolvedValue({ sessionId: 'session-1', chunkSize: 16, totalChunks: 1, uploadedChunks: [] }),
    putChunk: vi.fn().mockResolvedValue(undefined),
    merge: vi.fn().mockResolvedValue({}),
    abortUpload: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  } as Partial<OemApi>
}

function renderUploader(api: Partial<OemApi>) {
  return render(<OemProvider value={context(api)}><OemUploader transferId={41} /></OemProvider>)
}

afterEach(() => {
  vi.restoreAllMocks()
  usePortalAuth.setState({ token: null, account: null, mustChangePassword: false, booted: false, generation: 0 })
  window.localStorage.clear()
})

describe('A. OEM 分片 SHA-256 摘要', () => {
  beforeEach(() => { hashMocks.fileMd5.mockResolvedValue('md5') })

  it('OemApi.putChunk 发送 X-Chunk-SHA256 请求头', async () => {
    const put = vi.fn().mockResolvedValue({ data: {} })
    const api = new OemApi({ put } as unknown as AxiosInstance)
    await api.putChunk('s1', 3, new Blob(['abc']), ABC_SHA256, undefined, { quietNetworkError: true })
    const config = put.mock.calls[0][2]
    expect(put.mock.calls[0][0]).toBe('/oem/uploads/s1/chunks/3')
    expect(config.headers['X-Chunk-SHA256']).toBe(ABC_SHA256)
    expect(config.quietNetworkError).toBe(true)
  })

  it('上传队列用共享分片哈希工具计算摘要并随分片发送', async () => {
    const user = userEvent.setup()
    const api = uploadApi()
    renderUploader(api)
    await user.upload(screen.getByLabelText('选择文件'), new File(['abc'], '摘要.pdf'))
    expect(await screen.findByText('上传完成')).toBeInTheDocument()
    const [sessionId, index, , sha256] = vi.mocked(api.putChunk!).mock.calls[0]
    expect([sessionId, index, sha256]).toEqual(['session-1', 0, ABC_SHA256])
  })

  it('摘要不符时重新读取并重发一次，再次不符才失败', async () => {
    const user = userEvent.setup()
    const mismatch = { response: { status: 400, data: { message: '分片 SHA-256 校验失败' } } }
    const putChunk = vi.fn().mockRejectedValueOnce(mismatch).mockResolvedValue(undefined)
    const api = uploadApi({ putChunk } as Partial<OemApi>)
    renderUploader(api)
    await user.upload(screen.getByLabelText('选择文件'), new File(['abc'], '重发.pdf'))
    expect(await screen.findByText('上传完成')).toBeInTheDocument()
    expect(putChunk).toHaveBeenCalledTimes(2)

    const failing = vi.fn().mockRejectedValue(mismatch)
    renderUploader(uploadApi({ putChunk: failing } as Partial<OemApi>))
    await user.upload(screen.getAllByLabelText('选择文件')[1], new File(['abc'], '仍不符.pdf'))
    expect(await screen.findByText('分片 SHA-256 校验失败')).toBeInTheDocument()
    expect(failing).toHaveBeenCalledTimes(2)
  })
})

describe('B. 门户刷新锁与协作端隔离', () => {
  it('门户刷新使用独立的 Web Lock 名称', async () => {
    const names: string[] = []
    const descriptor = Object.getOwnPropertyDescriptor(navigator, 'locks')
    Object.defineProperty(navigator, 'locks', {
      configurable: true,
      value: { request: vi.fn(async (name: string) => { names.push(name); return false }) },
    })
    try {
      usePortalAuth.setState({ account, token: null, booted: false, generation: 1 })
      await bootPortalSession()
      expect(names).toEqual([PORTAL_AUTH_LOCK_NAME])
      expect(PORTAL_AUTH_LOCK_NAME).not.toBe('yf-auth-session')
    } finally {
      if (descriptor) Object.defineProperty(navigator, 'locks', descriptor)
      else Reflect.deleteProperty(navigator, 'locks')
    }
  })

  it('localStorage 租约：协作端租约不阻塞门户，门户内部仍串行', async () => {
    window.localStorage.setItem('yf:auth-refresh-lock', JSON.stringify({ owner: 'main-tab', expires: Date.now() + 90_000 }))
    expect(await withPortalStorageLease(async () => 'portal')).toBe('portal')
    expect(window.localStorage.getItem('yf:auth-refresh-lock')).toContain('main-tab')

    const order: string[] = []
    let release!: () => void
    const first = withPortalStorageLease(async () => {
      order.push('first:start')
      expect(window.localStorage.getItem(PORTAL_STORAGE_LOCK_KEY)).not.toBeNull()
      await new Promise<void>((resolve) => { release = resolve })
      order.push('first:end')
    })
    await waitFor(() => expect(order).toEqual(['first:start']))
    const second = withPortalStorageLease(async () => { order.push('second') })
    await new Promise((resolve) => setTimeout(resolve, 150))
    expect(order).toEqual(['first:start'])
    release()
    await Promise.all([first, second])
    expect(order).toEqual(['first:start', 'first:end', 'second'])
    expect(window.localStorage.getItem(PORTAL_STORAGE_LOCK_KEY)).toBeNull()
  })
})

describe('C. 自动重试期间不重复提示', () => {
  beforeEach(() => { hashMocks.fileMd5.mockResolvedValue('md5') })

  it('分片多次瞬断只在最终失败时提示一次', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const user = userEvent.setup()
    const putChunk = vi.fn().mockImplementation((...args: unknown[]) =>
      Promise.reject(Object.assign(new Error('Network Error'), { code: 'ERR_NETWORK', config: args[5] })))
    renderUploader(uploadApi({ putChunk } as Partial<OemApi>))
    await user.upload(screen.getByLabelText('选择文件'), new File(['abc'], '断网.pdf'))

    expect(await screen.findByText('Network Error', {}, { timeout: 5000 })).toBeInTheDocument()
    expect(putChunk).toHaveBeenCalledTimes(3)
    expect(toast).toHaveBeenCalledTimes(1)
    expect(String(toast.mock.calls[0][0])).toContain('断网.pdf')
  })

  it('门户 HTTP 层遵循 quietNetworkError / quietClientError', async () => {
    const toast = vi.spyOn(Message, 'error').mockImplementation(() => () => undefined)
    const failWith = (status: number): AxiosAdapter => async (config: InternalAxiosRequestConfig) => {
      throw new AxiosError('failed', undefined, config, undefined,
        { status, statusText: '', headers: {}, config, data: { message: `HTTP ${status}` } })
    }
    await expect(portalHttp.put('/oem/uploads/s/chunks/0', null, { adapter: failWith(503), quietNetworkError: true } as never)).rejects.toBeTruthy()
    await expect(portalHttp.put('/oem/uploads/s/chunks/0', null, { adapter: failWith(400), quietClientError: true } as never)).rejects.toBeTruthy()
    expect(toast).not.toHaveBeenCalled()
    await expect(portalHttp.put('/oem/uploads/s/chunks/0', null, { adapter: failWith(503) })).rejects.toBeTruthy()
    expect(toast).toHaveBeenCalledTimes(1)
  })
})

describe('D. OEM 预览带水印', () => {
  const file = (ext: string): TransferFile => ({ id: 88, originalName: `图纸.${ext}`, ext, sizeBytes: 3 } as TransferFile)

  it('门户图片在页内预览并显示当前厂商账号水印', async () => {
    usePortalAuth.setState({ account, token: 't' })
    const createUrl = vi.fn(() => 'blob:oem-image')
    Object.assign(URL, { createObjectURL: createUrl, revokeObjectURL: vi.fn() })
    const previewBlob = vi.fn().mockResolvedValue(new Blob(['png']))
    const view = render(<OemProvider value={context({ previewBlob }, 'oem')}><OemFilePreview file={file('png')} onClose={() => undefined} /></OemProvider>)

    await waitFor(() => expect(document.querySelector('img[src="blob:oem-image"]')).not.toBeNull())
    expect(previewBlob).toHaveBeenCalledWith(88, expect.any(AbortSignal))
    const watermark = document.querySelector('.preview-watermark')
    expect(watermark?.textContent).toContain('OEM31 厂商用户')
    view.unmount()
  })

  it('PDF 预览通过 OEM 内容接口取数并叠加水印', async () => {
    usePortalAuth.setState({ account, token: 't' })
    const previewBlob = vi.fn().mockResolvedValue(new Blob(['%PDF']))
    render(<OemProvider value={context({ previewBlob }, 'oem')}><OemFilePreview file={file('pdf')} onClose={() => undefined} /></OemProvider>)

    expect(await screen.findByTestId('pdf-preview')).toBeInTheDocument()
    expect(document.querySelector('.file-preview-surface .preview-watermark')?.textContent).toContain('OEM31 厂商用户')
    const bytes = await pdfProps.loadContent!(new AbortController().signal)
    expect(bytes.byteLength).toBe(4)
    expect(previewBlob).toHaveBeenCalledWith(88, expect.any(AbortSignal))
  })
})
