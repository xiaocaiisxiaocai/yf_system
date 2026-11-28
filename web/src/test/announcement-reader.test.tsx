import { StrictMode } from 'react'
import axios from 'axios'
import { QueryClientProvider } from '@tanstack/react-query'
import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../api/queryClient'
import AnnouncementCenter from '../announcements/AnnouncementCenter'
import AnnouncementDetail from '../announcements/AnnouncementDetail'
import { OemProvider } from '../oem/OemContext'
import { OemApi } from '../oem/api/OemApi'
import type { Realm } from '../oem/api/types'

const row = { id: 25, title: '包装要求', departmentName: '研发部', publishedAt: '2026-11-27T09:00:00Z',
  pinnedAt: '2026-11-27T09:04:00Z', isRead: false }
const body = '装箱时核对附件。'

function testInbox(realm: Realm, direct = false, recording?: Promise<void>) {
  const http = axios.create()
  const base = realm === 'oem' ? '/oem-portal' : '/oem'
  const endpoint = realm === 'oem' ? '/oem/announcements' : '/announcements'
  let withdrawn = false
  let read = false
  const get = vi.spyOn(http, 'get').mockImplementation(async (url, options) => {
    if (url === endpoint) {
      const filter = options?.params as { unreadOnly?: boolean } | undefined
      const visible = !withdrawn && (!filter?.unreadOnly || !read)
      return { data: { list: visible ? [{ ...row, isRead: read }] : [], total: visible ? 1 : 0,
        page: 1, pageSize: 100, unreadCount: withdrawn || read ? 0 : 1 } }
    }
    if (url === `${endpoint}/25` && !withdrawn) return { data: { ...row, body } }
    throw { response: { status: 404 } }
  })
  const post = vi.spyOn(http, 'post').mockImplementation(async (url) => {
    expect(screen.getByText(body)).toBeInTheDocument()
    expect(url).toBe(`${endpoint}/25/read`)
    await recording
    read = true
    return { data: {} }
  })
  const value = { api: new OemApi(http), base, realm, permissions: new Set<string>(), userId: 1, queryScope: [realm, 1] }
  const client = createAppQueryClient()
  const mount = () => render(<StrictMode>
    <QueryClientProvider client={client}><OemProvider value={value}>
      <MemoryRouter initialEntries={[`${base}/announcements${direct ? '/25' : ''}`]}>
        <Routes>
          <Route path={`${base}/announcements`} element={<AnnouncementCenter />} />
          <Route path={`${base}/announcements/:id`} element={<AnnouncementDetail />} />
        </Routes>
      </MemoryRouter>
    </OemProvider></QueryClientProvider>
  </StrictMode>)
  return { get, post, mount, client, endpoint, withdraw: () => { withdrawn = true } }
}

describe('公告读者端', () => {
  it.each(['internal', 'oem'] as const)('%s 读者可以按标题和未读筛选，实际打开正文后才记阅读', async (realm) => {
    const user = userEvent.setup()
    const { get, post, mount, endpoint } = testInbox(realm)
    mount()
    expect(await screen.findByText('未读 1 条')).toBeVisible()
    expect(screen.getByText('置顶')).toBeVisible()
    await user.type(screen.getByRole('textbox', { name: '搜索公告标题' }), '包装{Enter}')
    await user.click(screen.getByRole('checkbox', { name: '只看未读' }))
    await waitFor(() => expect(get).toHaveBeenCalledWith(endpoint, expect.objectContaining({
      params: expect.objectContaining({ keyword: '包装', unreadOnly: true }),
    })))
    expect(post).not.toHaveBeenCalled()
    await user.click(await screen.findByRole('link', { name: row.title }))
    expect(await screen.findByText(body)).toBeVisible()
    await waitFor(() => expect(post).toHaveBeenCalledTimes(1))
    await user.click(screen.getByRole('link', { name: '返回公告中心' }))
    expect(await screen.findByText('未读 0 条')).toBeVisible()
    expect(await screen.findByText('暂无未读公告')).toBeVisible()
  })

  it('下架后从相同门户链接再次进入，隐藏旧正文且不再记阅读', async () => {
    const { mount, post, withdraw } = testInbox('oem', true)
    const view = mount()
    await waitFor(() => expect(post).toHaveBeenCalledTimes(1))
    view.unmount()
    withdraw()
    mount()
    expect(await screen.findByText('此公告不可查看')).toBeVisible()
    expect(screen.queryByText(body)).not.toBeInTheDocument()
    expect(post).toHaveBeenCalledTimes(1)
  })

  it('后台刷新期间公告标题不会被加载遮罩挡住，失权后移除旧列表和未读数', async () => {
    const { mount, client, get } = testInbox('oem')
    mount()
    await screen.findByRole('link', { name: row.title })
    let deny!: () => void
    get.mockImplementationOnce(() => new Promise((_resolve, reject) => {
      deny = () => reject({ response: { status: 403 } })
    }))
    await act(async () => { void client.invalidateQueries({ queryKey: ['announcement-inbox'] }) })
    expect(client.isFetching()).toBe(1)
    expect(screen.getByRole('link', { name: row.title })).toBeVisible()
    expect(document.querySelector('.arco-spin-loading')).not.toBeInTheDocument()
    await act(async () => { deny() })
    expect(await screen.findByText('公告列表加载失败')).toBeVisible()
    expect(screen.queryByRole('link', { name: row.title })).not.toBeInTheDocument()
    expect(screen.queryByText('未读 1 条')).not.toBeInTheDocument()
  })

  it('读者在保存完成前返回列表时，保存成功会刷新未读数', async () => {
    let complete!: () => void
    const recording = new Promise<void>((resolve) => { complete = resolve })
    const { mount, post } = testInbox('internal', true, recording)
    const user = userEvent.setup()
    mount()
    await waitFor(() => expect(post).toHaveBeenCalledTimes(1))
    await user.click(screen.getByRole('link', { name: '返回公告中心' }))
    expect(await screen.findByText('未读 1 条')).toBeVisible()
    await act(async () => { complete(); await recording })
    expect(await screen.findByText('未读 0 条')).toBeVisible()
  })
})
