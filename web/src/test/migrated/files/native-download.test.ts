import { afterEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ post: vi.fn(), error: vi.fn() }))

vi.mock('../../../api/client', () => ({ default: { post: mocks.post } }))
vi.mock('@arco-design/web-react', () => ({ Message: { error: mocks.error } }))

import { downloadFile, downloadFiles } from '../../../api/download'

const handle = '0123456789abcdef0123456789abcdef'
const frames = () => Array.from(document.querySelectorAll<HTMLIFrameElement>('iframe.native-download-frame'))

// jsdom does not fetch frame sources, so write the error page the server would have rendered.
function failWith(frame: HTMLIFrameElement, body: string) {
  const page = frame.contentDocument!
  page.open()
  page.write('<!doctype html><html><body></body></html>')
  page.close()
  page.body.textContent = body
  frame.dispatchEvent(new Event('load'))
}

describe('native authenticated downloads', () => {
  afterEach(() => {
    for (const frame of frames()) frame.remove()
  })

  it('single download uses an authenticated grant then a hidden same-origin frame', async () => {
    mocks.post.mockResolvedValue({
      data: { url: `/api/v1/files/7/native-download/${handle}`, expiresInSeconds: 60 },
    })

    await downloadFile(7)

    expect(mocks.post).toHaveBeenCalledWith('/files/7/download-grant')
    const [frame] = frames()
    expect(frame.src).toBe(`${window.location.origin}/api/v1/files/7/native-download/${handle}`)
    expect(frame.hidden).toBe(true)
    expect(mocks.error).not.toHaveBeenCalled()
  })

  it('a JSON error page in the frame is shown with the server reason and the frame is removed', async () => {
    mocks.post.mockResolvedValue({
      data: { url: `/api/v1/files/7/native-download/${handle}`, expiresInSeconds: 60 },
    })
    await downloadFile(7)
    const [frame] = frames()

    failWith(frame, JSON.stringify({ code: 40101, message: '下载凭证无效、已使用或已过期' }))

    expect(mocks.error).toHaveBeenCalledWith('下载凭证无效、已使用或已过期')
    expect(frame.isConnected).toBe(false)
  })

  it('a non-JSON error page falls back to a generic message; an empty load is ignored', async () => {
    mocks.post.mockResolvedValue({
      data: { url: `/api/v1/files/batch-download/${handle}`, expiresInSeconds: 60 },
    })
    await downloadFiles([7])
    const [frame] = frames()

    frame.dispatchEvent(new Event('load'))
    expect(mocks.error).not.toHaveBeenCalled()
    expect(frame.isConnected).toBe(true)

    failWith(frame, 'Service Unavailable')
    expect(mocks.error).toHaveBeenCalledWith('下载失败，请稍后重试')
    expect(frame.isConnected).toBe(false)
  })

  it('the frame is cleaned up after its lifetime when the browser took over the download', async () => {
    vi.useFakeTimers()
    mocks.post.mockResolvedValue({
      data: { url: `/api/v1/files/7/native-download/${handle}`, expiresInSeconds: 60 },
    })
    await downloadFile(7)
    expect(frames()).toHaveLength(1)

    vi.advanceTimersByTime(10 * 60_000)

    expect(frames()).toHaveLength(0)
  })

  it('batch grant fixes the selected ids and rejects an off-origin response URL', async () => {
    mocks.post
      .mockResolvedValueOnce({ data: { url: `/api/v1/files/batch-download/${handle}`, expiresInSeconds: 60 } })
      .mockResolvedValueOnce({ data: { url: 'https://attacker.invalid/file', expiresInSeconds: 60 } })

    await downloadFiles([7, 9])
    await expect(downloadFile(7)).rejects.toThrow('服务端返回了无效的下载地址')

    expect(mocks.post).toHaveBeenNthCalledWith(1, '/files/batch-download-grant', { ids: [7, 9] })
    expect(frames()).toHaveLength(1)
  })
})
