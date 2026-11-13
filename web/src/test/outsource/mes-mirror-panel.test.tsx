import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import http from '../../api/client'
import MesMirrorPanel from '../../outsource/pages/MesMirrorPanel'
import { useAuth } from '../../store/auth'
import type { ApiResponses } from '../../api/types'

type Status = ApiResponses['GET /outsource/mes-mirror/status']
type Settings = ApiResponses['GET /outsource/mes-mirror/settings']

const idle: Status = {
  phase: 'IDLE', runMode: null, page: null, pageSize: 100, fetched: 0, pages: 0, total: null, pendingReason: null,
  startedAt: null, lastFinishedAt: '2026-11-13T03:10:00Z', lastSuccessAt: '2026-11-13T03:10:00Z', lastError: null, lastTrigger: 'SCHEDULED',
  lastCount: 1778, mirrorCount: 1778, preview: [],
}
const running: Status = { ...idle, phase: 'RUNNING', runMode: 'ALL', fetched: 200, pages: 2, total: 1778, startedAt: '2026-11-13T05:00:00Z' }
const preview = [
  { workOrderNo: '21074672', customerName: 'Delton Technology', deviceModels: 'CSLR-606AD' },
  { workOrderNo: '21074704', customerName: 'Delton Technology', deviceModels: 'CSUR-6638F' },
]
const stopped: Status = { ...idle, phase: 'AWAITING_CONFIRM', runMode: 'ALL', fetched: 200, pages: 2, total: 1778, pendingReason: 'STOPPED', preview }
const pageFetched: Status = { ...idle, phase: 'AWAITING_CONFIRM', runMode: 'PAGE', page: 3, pageSize: 50, fetched: 50, pages: 1, total: 1778, pendingReason: 'PAGE_FETCHED', preview }
const settings: Settings = { baseUrl: 'http://183.238.37.108:6001', configBaseUrl: 'http://183.238.37.108:6001', saved: false, accountConfigured: true }

function mock(status: Status, current: Settings = settings) {
  const get = vi.spyOn(http, 'get').mockImplementation((url: string) =>
    Promise.resolve({ data: url.endsWith('/settings') ? current : status } as never))
  const post = vi.spyOn(http, 'post').mockResolvedValue({ data: status } as never)
  const put = vi.spyOn(http, 'put').mockResolvedValue({ data: current } as never)
  return { get, post, put }
}

describe('MES 数据镜像手动抓取', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    useAuth.setState({ token: null, user: null, permissions: [], menus: [] })
  })

  it('空闲时显示镜像笔数和最近结果，并校验页码与每页条数', async () => {
    const user = userEvent.setup()
    const { post } = mock(idle)
    render(<MesMirrorPanel />)

    expect(await screen.findByText('1778')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '抓取全部' })).toBeEnabled()
    expect(screen.queryByRole('button', { name: '停止' })).not.toBeInTheDocument()

    const size = screen.getByRole('spinbutton', { name: '每页条数' })
    await user.clear(size)
    await user.type(size, '0')
    await user.tab()
    expect(screen.getByRole('button', { name: '抓取全部' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '抓取指定页' })).toBeDisabled()
    expect(post).not.toHaveBeenCalled()
  })

  it('抓取全部按每页条数发起，运行中显示进度并只能停止', async () => {
    const user = userEvent.setup()
    const { post } = mock(idle)
    render(<MesMirrorPanel />)
    await screen.findByText('1778')

    post.mockResolvedValueOnce({ data: running } as never)
    const size = screen.getByRole('spinbutton', { name: '每页条数' })
    await user.clear(size)
    await user.type(size, '50')
    await user.click(screen.getByRole('button', { name: '抓取全部' }))

    expect(post).toHaveBeenCalledWith('/outsource/mes-mirror/run', { mode: 'ALL', pageSize: 50 })
    expect(await screen.findByText(/已抓取 200 \/ 1778 笔/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '停止' })).toBeEnabled()
    expect(screen.getByRole('button', { name: '抓取全部' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '抓取指定页' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '测试连接' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '保存地址' })).toBeDisabled()
  })

  it('停止后要二次确认：合并已抓部分', async () => {
    const user = userEvent.setup()
    const { post } = mock(running)
    render(<MesMirrorPanel />)
    post.mockResolvedValueOnce({ data: stopped } as never)
    await user.click(await screen.findByRole('button', { name: '停止' }))
    expect(post).toHaveBeenCalledWith('/outsource/mes-mirror/stop')

    const dialog = await screen.findByRole('dialog', { name: '确认抓取结果' })
    expect(within(dialog).getByText(/已抓取 200 笔/)).toBeInTheDocument()
    expect(within(dialog).getByText('21074672')).toBeInTheDocument()
    expect(within(dialog).getByText('CSUR-6638F')).toBeInTheDocument()

    post.mockResolvedValueOnce({ data: { ...idle, mirrorCount: 1780 } } as never)
    await user.click(within(dialog).getByRole('button', { name: '合并已抓部分' }))
    expect(post).toHaveBeenLastCalledWith('/outsource/mes-mirror/confirm', { apply: true })
    await waitFor(() => expect(screen.queryByRole('dialog', { name: '确认抓取结果' })).not.toBeInTheDocument())
  })

  it('放弃不改动镜像：发送 apply=false', async () => {
    const user = userEvent.setup()
    const { post } = mock(stopped)
    render(<MesMirrorPanel />)
    const dialog = await screen.findByRole('dialog', { name: '确认抓取结果' })
    post.mockResolvedValueOnce({ data: idle } as never)
    await user.click(within(dialog).getByRole('button', { name: '放弃，保留现有数据' }))
    expect(post).toHaveBeenLastCalledWith('/outsource/mes-mirror/confirm', { apply: false })
    await waitFor(() => expect(screen.queryByRole('dialog', { name: '确认抓取结果' })).not.toBeInTheDocument())
  })

  it('抓取指定页后同样弹出确认并写明页码；加载时已在等待确认也直接显示', async () => {
    const user = userEvent.setup()
    const { post } = mock(idle)
    const view = render(<MesMirrorPanel />)
    await screen.findByText('1778')

    post.mockResolvedValueOnce({ data: pageFetched } as never)
    const page = screen.getByRole('spinbutton', { name: '页码' })
    await user.clear(page)
    await user.type(page, '3')
    await user.click(screen.getByRole('button', { name: '抓取指定页' }))
    expect(post).toHaveBeenCalledWith('/outsource/mes-mirror/run', { mode: 'PAGE', page: 3, pageSize: 100 })
    const dialog = await screen.findByRole('dialog', { name: '确认抓取结果' })
    expect(within(dialog).getByText(/第 3 页/)).toBeInTheDocument()
    view.unmount()

    vi.restoreAllMocks()
    mock(pageFetched)
    render(<MesMirrorPanel />)
    expect(await screen.findByRole('dialog', { name: '确认抓取结果' })).toBeInTheDocument()
  })

  it('MES 地址可在页面修改并保存，留空保存即恢复配置值', async () => {
    const user = userEvent.setup()
    const { put } = mock(idle)
    render(<MesMirrorPanel />)
    const address = await screen.findByRole('textbox', { name: 'MES 地址' })
    expect(address).toHaveValue('http://183.238.37.108:6001')
    expect(screen.getByRole('button', { name: '保存地址' })).toBeDisabled()

    put.mockResolvedValueOnce({ data: { ...settings, baseUrl: 'http://10.0.0.5:6001', saved: true } } as never)
    await user.clear(address)
    await user.type(address, '  http://10.0.0.5:6001/ ')
    await user.click(screen.getByRole('button', { name: '保存地址' }))
    expect(put).toHaveBeenCalledWith('/outsource/mes-mirror/settings', { baseUrl: 'http://10.0.0.5:6001/' })
    await waitFor(() => expect(address).toHaveValue('http://10.0.0.5:6001'))

    put.mockResolvedValueOnce({ data: settings } as never)
    await user.clear(address)
    await user.click(screen.getByRole('button', { name: '保存地址' }))
    expect(put).toHaveBeenLastCalledWith('/outsource/mes-mirror/settings', { baseUrl: '' })
    await waitFor(() => expect(address).toHaveValue('http://183.238.37.108:6001'))
  })

  it('测试连接用输入框里的地址，显示成功的笔数与耗时或失败原因，不保存地址', async () => {
    const user = userEvent.setup()
    const { post, put } = mock(idle)
    render(<MesMirrorPanel />)
    const address = await screen.findByRole('textbox', { name: 'MES 地址' })
    await user.clear(address)
    await user.type(address, 'http://10.0.0.5:6001')

    post.mockResolvedValueOnce({ data: { ok: true, message: '连接成功，MES 共 1778 笔', total: 1778, elapsedMs: 820 } } as never)
    await user.click(screen.getByRole('button', { name: '测试连接' }))
    expect(post).toHaveBeenCalledWith('/outsource/mes-mirror/test', { baseUrl: 'http://10.0.0.5:6001' }, { timeout: 200_000 })
    expect(await screen.findByText(/连接成功，MES 共 1778 笔/)).toBeInTheDocument()
    expect(screen.getByText(/0\.8 秒/)).toBeInTheDocument()

    post.mockResolvedValueOnce({ data: { ok: false, message: 'MES 登录失败，请检查专用账号配置', total: null, elapsedMs: 300 } } as never)
    await user.click(screen.getByRole('button', { name: '测试连接' }))
    expect(await screen.findByText(/MES 登录失败，请检查专用账号配置/)).toBeInTheDocument()
    expect(put).not.toHaveBeenCalled()
  })

  it('专用账号未配置时给出提示', async () => {
    mock(idle, { ...settings, accountConfigured: false })
    render(<MesMirrorPanel />)
    expect(await screen.findByText('专用账号未配置')).toBeInTheDocument()
  })
})
