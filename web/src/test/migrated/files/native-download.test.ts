import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ post: vi.fn() }))

vi.mock('../../../api/client', () => ({ default: { post: mocks.post } }))

import { downloadFile, downloadFiles } from '../../../api/download'

describe('native authenticated downloads', () => {
  let click: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
  })

  afterEach(() => {
    click.mockRestore()
  })

  it('single download uses an authenticated grant then a same-origin native href', async () => {
    const handle = '0123456789abcdef0123456789abcdef'
    mocks.post.mockResolvedValue({
      data: { url: `/api/v1/files/7/native-download/${handle}`, expiresInSeconds: 60 },
    })
    const appended: HTMLAnchorElement[] = []
    const append = vi.spyOn(document.body, 'appendChild').mockImplementation((node) => {
      appended.push(node as HTMLAnchorElement)
      return node
    })

    await downloadFile(7)

    expect(mocks.post).toHaveBeenCalledWith('/files/7/download-grant')
    expect(appended[0]).toMatchObject({
      href: `${window.location.origin}/api/v1/files/7/native-download/${handle}`,
      download: '',
      rel: 'noopener',
    })
    expect(click).toHaveBeenCalledOnce()
    expect(appended[0].isConnected).toBe(false)
    append.mockRestore()
  })

  it('batch grant fixes the selected ids and rejects an off-origin response URL', async () => {
    mocks.post
      .mockResolvedValueOnce({
        data: { url: '/api/v1/files/batch-download/0123456789abcdef0123456789abcdef', expiresInSeconds: 60 },
      })
      .mockResolvedValueOnce({ data: { url: 'https://attacker.invalid/file', expiresInSeconds: 60 } })

    await downloadFiles([7, 9])
    await expect(downloadFile(7)).rejects.toThrow('服务端返回了无效的下载地址')

    expect(mocks.post).toHaveBeenNthCalledWith(1, '/files/batch-download-grant', { ids: [7, 9] })
    expect(click).toHaveBeenCalledOnce()
  })
})
