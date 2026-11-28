import { QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../api/queryClient'
import { useAuth } from '../store/auth'
import AnnouncementEditor from '../announcements/AnnouncementEditor'
import AnnouncementManagementPage from '../announcements/AnnouncementManagementPage'
import { managementApi, type ManageDetail } from '../announcements/managementApi'

const options = { departmentId: 4, departmentName: '研发部', departments: [{ id: 4, name: '研发部' }, { id: 7, name: '采购部' }],
  companies: [{ id: 8, name: '甲公司', accountCount: 2 }, { id: 9, name: '乙公司', accountCount: 0 }] }
const source: ManageDetail = {
  id: 12, version: 3, title: '发货要求', body: '包装规则已更新', departmentId: 4, departmentName: '研发部',
  status: 'WITHDRAWN', internalScope: 'OWN', externalScope: 'SELECTED', departments: [], companies: [{ id: 8, name: '甲公司' }],
  publisherId: 1, publisherName: '甲员工', publishedAt: '2026-11-26T09:00:00Z', updatedAt: '2026-11-26T15:00:00Z',
  pinnedAt: null, withdrawnAt: '2026-11-26T15:00:00Z', withdrawnByName: '甲员工',
  canEdit: false, canPublish: false, canWithdraw: false, canPin: false, canReadDetails: true,
}
const click = (button: string) => fireEvent.click(screen.getByRole('button', { name: button }))
const radio = (label: string) => screen.getByRole('radio', { name: label })
const mountPage = (client = createAppQueryClient()) => render(<QueryClientProvider client={client}><AnnouncementManagementPage /></QueryClientProvider>)

afterEach(() => vi.restoreAllMocks())

describe('公告管理端', () => {
  it('后台刷新不遮挡复制操作，网络失败保留结果并允许重试', async () => {
    useAuth.setState({ permissions: ['announcement:publish'] })
    const result = { list: [source], total: 1, page: 1, pageSize: 100 }
    let failRefresh!: (error: unknown) => void
    const list = vi.spyOn(managementApi, 'list').mockResolvedValue(result)
      .mockResolvedValueOnce(result).mockImplementationOnce(() => new Promise((_resolve, reject) => { failRefresh = reject }))
    vi.spyOn(managementApi, 'options').mockResolvedValue(options)
    vi.spyOn(managementApi, 'detail').mockResolvedValue(source)
    const client = createAppQueryClient()
    const view = mountPage(client)
    const copy = await screen.findByRole('button', { name: '复制为草稿' })
    await act(async () => { void client.refetchQueries({ queryKey: ['announcement-management-list'] }) })
    expect(list).toHaveBeenCalledTimes(2)
    expect(view.container.querySelector('.arco-spin-loading-layer')).not.toBeInTheDocument()
    fireEvent.click(copy)
    expect(await screen.findByText('复制为新公告')).toBeVisible()
    await act(async () => failRefresh({ code: 'ERR_NETWORK' }))
    expect(await screen.findByText('刷新失败，当前显示上次加载的公告。')).toBeInTheDocument()
    expect(copy).toBeInTheDocument()
    click('取消')
    click('重试')
    await waitFor(() => expect(screen.queryByText('刷新失败，当前显示上次加载的公告。')).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: '复制为草稿' })).toBeVisible()
  })

  it.each([401, 403])('后台返回 %s 后移除缓存公告、操作和弹窗，断网重试不能恢复它们', async status => {
    useAuth.setState({ permissions: ['announcement:publish'] })
    const result = { list: [source], total: 1, page: 1, pageSize: 100 }
    const list = vi.spyOn(managementApi, 'list').mockResolvedValueOnce(result)
      .mockRejectedValueOnce({ response: { status } }).mockRejectedValueOnce({ code: 'ERR_NETWORK' })
    vi.spyOn(managementApi, 'options').mockResolvedValue(options)
    vi.spyOn(managementApi, 'detail').mockResolvedValue(source)
    const client = createAppQueryClient()
    mountPage(client)
    fireEvent.click(await screen.findByRole('button', { name: '复制为草稿' }))
    expect(await screen.findByText('复制为新公告')).toBeVisible()
    await act(async () => { await client.refetchQueries({ queryKey: ['announcement-management-list'] }) })
    expect(await screen.findByText('公告管理列表加载失败')).toBeVisible()
    for (const name of ['新建公告', '保存草稿', '发货要求']) expect(screen.queryByRole('button', { name })).not.toBeInTheDocument()
    click('重试')
    await waitFor(() => {
      expect(list).toHaveBeenCalledTimes(3)
      expect(client.isFetching({ queryKey: ['announcement-management-list'] })).toBe(0)
    })
    expect(screen.queryByRole('button', { name: '复制为草稿' })).not.toBeInTheDocument()
  })

  it('新公告默认本部门，外部必选，发布前预览列出无账号厂商，发布重试复用已保存草稿', async () => {
    const create = vi.spyOn(managementApi, 'create').mockResolvedValue({ ...source, id: 21, version: 1 })
    const publish = vi.spyOn(managementApi, 'publish').mockRejectedValueOnce(new Error('暂时无法发布')).mockResolvedValue(undefined)
    const saved = vi.fn()
    render(<AnnouncementEditor copy={false} options={options} onClose={vi.fn()} onSaved={saved} />)
    expect(radio('本部门')).toBeChecked()
    expect(radio('全部 ODM/OEMA 公司')).not.toBeChecked()
    fireEvent.change(screen.getByRole('textbox', { name: '公告标题' }), { target: { value: '收货要求' } })
    fireEvent.change(screen.getByRole('textbox', { name: '公告正文' }), { target: { value: '请留存记录' } })
    click('预览发布')
    expect(screen.getByText('请选择全部或指定 ODM/OEMA 公司')).toBeVisible()
    expect(create).not.toHaveBeenCalled()
    fireEvent.click(radio('全部 ODM/OEMA 公司'))
    fireEvent.click(radio('指定部门'))
    click('预览发布')
    expect(screen.getByText('请选择有效的内部部门')).toBeVisible()
    fireEvent.click(radio('全体内部人员'))
    expect(radio('全部 ODM/OEMA 公司')).toBeChecked()
    click('预览发布')
    expect(screen.getByText('暂无可登录账号：乙公司')).toBeVisible()
    expect(screen.getByText('全部 ODM/OEMA 公司（2 家）')).toBeVisible()
    click('确认发布')
    expect(await screen.findByText('暂时无法发布')).toBeVisible()
    click('确认发布')
    await waitFor(() => expect(saved).toHaveBeenCalledTimes(1))
    expect(create).toHaveBeenCalledTimes(1)
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ internalScope: 'ALL', externalScope: 'ALL' }))
    expect(publish.mock.calls).toEqual([[21, 1], [21, 1]])
  })

  it('复制归档公告生成新草稿，保留原正文与指定公司并使用当前部门署名', async () => {
    const create = vi.spyOn(managementApi, 'create').mockResolvedValue({ ...source, id: 22 })
    const update = vi.spyOn(managementApi, 'update')
    const saved = vi.fn()
    render(<AnnouncementEditor copy source={source} options={{ ...options, departmentId: 7, departmentName: '采购部' }} onClose={vi.fn()} onSaved={saved} />)
    expect(screen.getByText('采购部')).toBeVisible()
    expect(radio('指定 ODM/OEMA 公司')).toBeChecked()
    click('保存草稿')
    await waitFor(() => expect(saved).toHaveBeenCalledTimes(1))
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ title: source.title, body: source.body, companyIds: [8] }))
    expect(update).not.toHaveBeenCalled()
  })

  it('仅明细权限可以查看已下架公告的读者，页面不请求发布目录', async () => {
    useAuth.setState({ permissions: ['announcement:reads'] })
    vi.spyOn(managementApi, 'list').mockResolvedValue({ list: [source], total: 1, page: 1, pageSize: 100 })
    const optionsCall = vi.spyOn(managementApi, 'options')
    vi.spyOn(managementApi, 'reads').mockResolvedValue({ list: [{
      realm: 'oem', accountId: 8, accountName: 'odm01', displayName: '甲公司账号', firstReadAt: '2026-11-26T10:00:00Z', lastReadAt: '2026-11-26T12:00:00Z',
    }, {
      realm: 'internal', accountId: 8, accountName: 'staff01', displayName: '内部员工', firstReadAt: '2026-11-26T09:00:00Z', lastReadAt: '2026-11-26T09:00:00Z',
    }], total: 2, page: 1, pageSize: 100 })
    mountPage()
    fireEvent.click(await screen.findByRole('button', { name: '阅读明细' }))
    expect(await screen.findByText('odm01')).toBeVisible()
    expect(screen.getByText('内部账号')).toBeVisible()
    expect(screen.getByText('ODM/OEMA')).toBeVisible()
    expect(screen.queryByRole('button', { name: '置顶' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '新建公告' })).not.toBeInTheDocument()
    expect(optionsCall).not.toHaveBeenCalled()
  })

  it('同部门也不能下架他人公告，自己的在架公告需要确认', async () => {
    useAuth.setState({ permissions: ['announcement:withdraw'] })
    const other = { ...source, status: 'PUBLISHED', id: 13, title: '同事公告', canReadDetails: false }
    const mine = { ...other, id: 14, title: '本人公告', canWithdraw: true }
    vi.spyOn(managementApi, 'list').mockResolvedValue({ list: [other, mine], total: 2, page: 1, pageSize: 100 })
    const withdraw = vi.spyOn(managementApi, 'withdraw').mockResolvedValue(undefined)
    mountPage()
    const buttons = await screen.findAllByRole('button', { name: '下架' })
    expect(buttons[0]).toBeDisabled()
    fireEvent.click(buttons[1])
    expect(withdraw).not.toHaveBeenCalled()
    click('确认下架')
    await waitFor(() => expect(withdraw).toHaveBeenCalledWith(14))
  })
})
