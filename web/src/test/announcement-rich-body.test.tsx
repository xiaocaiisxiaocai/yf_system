import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import axios from 'axios'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../api/queryClient'
import { OemProvider } from '../oem/OemContext'
import { OemApi } from '../oem/api/OemApi'
import AnnouncementCenter from '../announcements/AnnouncementCenter'
import AnnouncementRichBody from '../announcements/AnnouncementRichBody'
import AnnouncementAssetImage from '../announcements/AnnouncementAssetImage'
import { loadAnnouncementAsset, type AnnouncementAccess } from '../announcements/announcementAssetContent'
import { saveBlob } from '../utils/saveBlob'

vi.mock('../utils/saveBlob', () => ({ saveBlob: vi.fn() }))

const assets = [
  { id: 3, kind: 'IMAGE', fileName: 'old.png', sizeBytes: 70, contentType: 'image/png' },
  { id: 4, kind: 'IMAGE', fileName: 'new.png', sizeBytes: 80, contentType: 'image/png' },
  { id: 7, kind: 'ATTACHMENT', fileName: '标签.doc', sizeBytes: 4096, contentType: 'application/msword' },
  { id: 8, kind: 'ATTACHMENT', fileName: '清单.xlsx', sizeBytes: 80, contentType: 'application/octet-stream' },
]
const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
const richDocument = JSON.stringify({ type: 'doc', content: [
  { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: '包装标签' }] },
  { type: 'orderedList', attrs: { start: 3 }, content: [{ type: 'listItem', content: [paragraph('按工令数量装箱')] }] },
  { type: 'image', attrs: { assetId: 3, caption: '旧标签', width: 50 } },
  { type: 'image', attrs: { assetId: 4, caption: '新标签', width: 75 } },
  { type: 'table', content: [{ type: 'tableRow', content: [{ type: 'tableCell', content: [paragraph('工令编号')] }] }] },
  { type: 'paragraph', content: [
    { type: 'text', text: '可查看说明', marks: [{ type: 'bold' }, { type: 'link', attrs: { href: 'https://example.test/guide' } }] },
    { type: 'text', text: '普通文本', marks: [{ type: 'link', attrs: { href: 'javascript:invalid' } }] },
  ] },
] })

function setupFiles() {
  const client = axios.create()
  const blob = new Blob(['asset'])
  const get = vi.spyOn(client, 'get').mockResolvedValue({ data: blob })
  let serial = 0
  const revoke = vi.fn()
  Object.assign(URL, { createObjectURL: vi.fn(() => `blob:ann-${++serial}`), revokeObjectURL: revoke })
  return { client, get, blob, revoke }
}
afterEach(() => vi.restoreAllMocks())

it.each([
  ['manage', '/announcements/manage'], ['internal', '/announcements'], ['oem', '/oem/announcements'],
] as const)('%s 使用对应会话的资产接口加载缩略图，卸载后释放本地 URL', async (access, prefix) => {
  const { client, get, revoke } = setupFiles()
  const view = render(<AnnouncementAssetImage assetId={3} announcementId={22} alt="公告首图"
    client={client} access={access} thumbnail />)
  expect(await screen.findByRole('img', { name: '公告首图' })).toHaveAttribute('src', 'blob:ann-1')
  expect(get).toHaveBeenCalledWith(`${prefix}/22/assets/3`, expect.objectContaining({
    responseType: 'blob', signal: expect.any(AbortSignal), params: expect.objectContaining({ thumbnail: true }),
  }))
  view.unmount()
  expect(revoke).toHaveBeenCalledWith('blob:ann-1')
})

it('共享正文保留排版，图片在正文内可放大切换，附件按原顺序下载', async () => {
  const { client, get, blob } = setupFiles()
  const user = userEvent.setup()
  const view = render(<AnnouncementRichBody body="" documentJson={richDocument}
    assets={[assets[1], assets[0], ...assets.slice(2)]}
    announcementId={22} access="oem" client={client} />)
  expect(screen.getByRole('heading', { name: '包装标签', level: 2 })).toBeVisible()
  expect(screen.getByRole('link', { name: '可查看说明' })).toHaveAttribute('href', 'https://example.test/guide')
  expect(screen.queryByRole('link', { name: '普通文本' })).toBeNull()
  expect(screen.getByRole('cell', { name: '工令编号' })).toBeVisible()
  expect(view.container.querySelector('ol')).toHaveAttribute('start', '3')
  expect([...view.container.querySelectorAll('figcaption')].map(c => c.textContent)).toEqual(['旧标签', '新标签'])
  expect(view.container.querySelector('figure')).toHaveStyle({ width: '50%' })
  const first = await screen.findByRole('button', { name: '放大图片：旧标签' })
  await user.click(first)
  const dialog = await screen.findByRole('dialog')
  expect(within(dialog).getByText('预览：old.png')).toBeVisible()
  await user.click(within(dialog).getByRole('button', { name: '下一张' }))
  expect(within(dialog).getByText('预览：new.png')).toBeVisible()
  expect(get).toHaveBeenCalledWith('/oem/announcements/22/assets/4', expect.objectContaining({ responseType: 'blob' }))
  await user.click(within(dialog).getByRole('button', { name: '关闭文件预览' }))
  expect(screen.queryByRole('button', { name: '预览 标签.doc' })).toBeNull()
  expect(screen.getByRole('button', { name: '预览 清单.xlsx' })).toBeVisible()
  expect([...view.container.querySelectorAll('.announcement-attachment > span')].map(c => c.textContent)).toEqual(['标签.doc', '清单.xlsx'])
  await user.click(screen.getByRole('button', { name: '下载 清单.xlsx' }))
  expect(saveBlob).toHaveBeenCalledWith(blob, '清单.xlsx')
  expect(get).toHaveBeenCalledWith('/oem/announcements/22/assets/8', expect.objectContaining({ params: expect.objectContaining({ download: true }) }))
})

it('历史纯文和仅附件公告正常显示；缺失的外部会话不会使用内部账号读取', async () => {
  const { client } = setupFiles()
  const props = { body: '历史公告\n第二行', announcementId: 22, access: 'manage' as AnnouncementAccess, client }
  const view = render(<AnnouncementRichBody {...props} />)
  expect(screen.getByText('历史公告 第二行')).toBeVisible()
  view.rerender(<AnnouncementRichBody {...props} body="" assets={assets.slice(2)} />)
  expect(screen.queryByText('历史公告 第二行')).toBeNull()
  expect(screen.getByText('附件（2）')).toBeVisible()
  await expect(loadAnnouncementAsset(22, 3, new AbortController().signal, { access: 'oem' })).rejects.toThrow('缺少厂商登录会话')
})

it('未获授权的图片取图失败后只显示重试，重试用当前权限重新请求', async () => {
  const { client, get } = setupFiles()
  get.mockRejectedValueOnce({ response: { status: 404 } })
  render(<AnnouncementAssetImage assetId={3} announcementId={22} alt="公告首图" client={client} access="oem" thumbnail />)
  expect(await screen.findByRole('button', { name: '重试图片' })).toBeVisible()
  expect(screen.queryByRole('img')).toBeNull()
  await userEvent.click(screen.getByRole('button', { name: '重试图片' }))
  await waitFor(() => expect(screen.getByRole('img', { name: '公告首图' })).toBeVisible())
})

it('厂商列表的纯图片和仅附件摘要只取首图缩略图，不请求正文或登记阅读', async () => {
  const { client, get, blob } = setupFiles()
  const endpoint = '/oem/announcements'
  const row = { id: 41, title: '纯图片', publishedAt: '2026-11-27T09:00:00Z', departmentName: '研发部',
    isRead: false, pinnedAt: null, preview: '', imageCount: 2, firstImageId: 3, attachmentCount: 0, firstAttachmentName: null }
  const post = vi.spyOn(client, 'post')
  get.mockImplementation(async url => {
    if (url === endpoint) return { data: { total: 2, unreadCount: 2, list: [
      row, { ...row, id: 42, title: '仅附件', firstImageId: null, imageCount: 0, attachmentCount: 2, firstAttachmentName: '包装清单.xlsx' },
    ] } }
    if (url === endpoint + '/41/assets/3') return { data: blob }
    throw new Error('意外的详情或附件请求')
  })
  render(<MemoryRouter><QueryClientProvider client={createAppQueryClient()}>
    <OemProvider value={{ api: new OemApi(client), base: '/oem-portal', realm: 'oem', userId: 12, permissions: new Set(), queryScope: ['oem', 12] }}>
      <AnnouncementCenter />
    </OemProvider></QueryClientProvider></MemoryRouter>)
  expect(await screen.findByText('共 2 张图片')).toBeVisible()
  expect(await screen.findByRole('img', { name: '公告首图' })).toHaveAttribute('src', 'blob:ann-1')
  expect(screen.getByText('包装清单.xlsx')).toBeVisible()
  expect(screen.getByText('共 2 个附件')).toBeVisible()
  expect(get.mock.calls.map(([url]) => url)).toEqual([endpoint, endpoint + '/41/assets/3'])
  expect(post).not.toHaveBeenCalled()
})
