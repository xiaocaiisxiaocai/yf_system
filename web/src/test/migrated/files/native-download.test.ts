import { afterEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ post: vi.fn(), error: vi.fn() }))

vi.mock('../../../api/client', () => ({ default: { post: mocks.post } }))
vi.mock('@arco-design/web-react', () => ({ Message: { error: mocks.error } }))

import { downloadFile, downloadFiles, startOemDownload } from '../../../api/download'

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

  describe('ODM/OEMA 下载授权', () => {
    it('只接受该文件自己的下载路径，并经隐藏 iframe 下载而不是整页导航', () => {
      startOemDownload('/api/v1/oem/files/301/download', 301)

      const [frame] = frames()
      expect(frame.src).toBe(`${window.location.origin}/api/v1/oem/files/301/download`)
      expect(frame.hidden).toBe(true)
    })

    it('ODM/OEMA 下载失败的 JSON 错误页只提示原因，不跳转页面', () => {
      startOemDownload('/api/v1/oem/files/301/download', 301)
      const [frame] = frames()
      const before = window.location.href

      failWith(frame, JSON.stringify({ code: 40401, message: '文件已被清理' }))

      expect(mocks.error).toHaveBeenCalledWith('文件已被清理')
      expect(frame.isConnected).toBe(false)
      expect(window.location.href).toBe(before)
    })

    it.each([
      ['https://attacker.invalid/api/v1/oem/files/301/download'],
      ['/api/v1/oem/files/302/download'],
      ['/api/v1/oem/files/301/download?x=1'],
      ['/api/v1/oem/files/301/download#frag'],
      ['/api/v1/oem/files/301/download/extra'],
      ['/api/v1/files/301/native-download/' + handle],
      ['//attacker.invalid/api/v1/oem/files/301/download'],
    ])('拒绝不符合期望形态的地址 %s', (url) => {
      expect(() => startOemDownload(url, 301)).toThrow('服务端返回了无效的下载地址')
      expect(frames()).toHaveLength(0)
    })

    it('拒绝非法的文件编号', () => {
      expect(() => startOemDownload('/api/v1/oem/files/0/download', 0)).toThrow('服务端返回了无效的下载地址')
      expect(() => startOemDownload('/api/v1/oem/files/1.*/download', '1.*')).toThrow('服务端返回了无效的下载地址')
    })
  })
})
