import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import http from '../api/client'
import AdminLayout from '../layouts/AdminLayout'
import AppRail from '../layouts/AppRail'
import NavSider, { NavMenu } from '../layouts/NavSider'
import NotFound from '../pages/NotFound'
import { PreviewWatermark } from '../components/PreviewWatermark'
import { useAuth, type UserBrief } from '../store/auth'
import { useCollaboration } from '../store/collaboration'
import { resetSendQueueForTests, useSendQueue } from '../outsource/sendQueue'

const internalUser: UserBrief = {
  id: 91,
  employeeNo: 'E091',
  realName: '壳层测试员',
  email: 'shell@example.test',
  userType: 'INTERNAL',
  supplierId: null,
  isSystemAdmin: false,
}

function LocationProbe() {
  const location = useLocation()
  return <output aria-label="当前路径">{location.pathname}</output>
}

describe('壳层导航、404 与水印修复回归', () => {
  beforeEach(() => {
    resetSendQueueForTests()
    useCollaboration.getState().reset()
    useAuth.setState({
      token: 'shell-token',
      user: internalUser,
      menus: ['dashboard', 'project:list'],
      permissions: ['project:list', 'oem:transfer_view', 'outsource:view', 'outsource:send'],
      mustChangePassword: false,
      booted: true,
      generation: 91,
    })
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    resetSendQueueForTests()
    useCollaboration.getState().reset()
  })

  it('NotFound 在厂商门户路径下返回文件传递，其余路径返回工作台', async () => {
    const user = userEvent.setup()
    const portal = render(
      <MemoryRouter initialEntries={['/oem-portal/missing']}>
        <Routes>
          <Route path="/oem-portal/transfers" element={<LocationProbe />} />
          <Route path="*" element={<><NotFound /><LocationProbe /></>} />
        </Routes>
      </MemoryRouter>,
    )
    await user.click(screen.getByRole('button', { name: '返回文件传递' }))
    expect(screen.getByRole('status', { name: '当前路径' })).toHaveTextContent('/oem-portal/transfers')
    portal.unmount()

    render(
      <MemoryRouter initialEntries={['/missing']}>
        <Routes>
          <Route path="/" element={<LocationProbe />} />
          <Route path="*" element={<><NotFound /><LocationProbe /></>} />
        </Routes>
      </MemoryRouter>,
    )
    await user.click(screen.getByRole('button', { name: '返回工作台' }))
    expect(screen.getByRole('status', { name: '当前路径' })).toHaveTextContent('/')
  })

  it('PreviewWatermark 默认渲染足够平铺并在大屏尺寸下动态补齐水印数量', () => {
    const callbacks: ResizeObserverCallback[] = []
    class FakeResizeObserver {
      constructor(cb: ResizeObserverCallback) { callbacks.push(cb) }
      observe() {}
      disconnect() {}
      unobserve() {}
    }
    vi.stubGlobal('ResizeObserver', FakeResizeObserver)

    const { container } = render(<PreviewWatermark employeeNo="E091" realName="壳层测试员" />)
    const watermark = container.querySelector<HTMLElement>('.preview-watermark')!
    expect(watermark.querySelectorAll('span').length).toBeGreaterThanOrEqual(24)

    Object.defineProperty(watermark, 'clientWidth', { configurable: true, value: 2560 })
    Object.defineProperty(watermark, 'clientHeight', { configurable: true, value: 1440 })
    act(() => {
      callbacks.forEach((cb) => cb([], {} as ResizeObserver))
    })
    expect(watermark.querySelectorAll('span').length).toBeGreaterThanOrEqual(80)
  })

  it('NavMenu 折叠状态下有待办角标时在图标角落显示红点', () => {
    const { container } = render(
      <NavMenu
        collapsed
        selectedKey="/outsource/items"
        onSelect={() => undefined}
        items={[{ key: '/outsource/items', label: '外发工令', icon: <span data-testid="nav-icon" />, badge: 5 }]}
      />,
    )
    expect(container.querySelector('.nav-menu-badge-dot .arco-badge-dot')).toBeInTheDocument()
  })

  it('NavSider 折叠按钮同步 aria-expanded 并在折叠态展示展开提示', async () => {
    localStorage.removeItem('yf:sider-collapsed')
    const user = userEvent.setup()
    try {
      render(
        <NavSider
          selectedKey="/outsource/items"
          onSelect={() => undefined}
          items={[{ key: '/outsource/items', label: '外发工令', icon: <span data-testid="nav-icon" /> }]}
        />,
      )
      const collapseButton = screen.getByRole('button', { name: '折叠侧边栏' })
      expect(collapseButton).toHaveAttribute('aria-expanded', 'true')

      await user.click(collapseButton)
      const expandButton = screen.getByRole('button', { name: '展开侧边栏' })
      expect(expandButton).toHaveAttribute('aria-expanded', 'false')

      await user.unhover(expandButton)
      await user.hover(expandButton)
      expect(await screen.findByText('展开侧边栏')).toBeVisible()

      await user.click(expandButton)
      expect(screen.getByRole('button', { name: '折叠侧边栏' })).toHaveAttribute('aria-expanded', 'true')
    } finally {
      localStorage.removeItem('yf:sider-collapsed')
    }
  })

  it('AppRail 展示两个模块，在侧栏和抽屉中保留跨模块未读数和工令队列角标', async () => {
    useSendQueue.setState({ count: 4 })
    vi.spyOn(http, 'get').mockImplementation(async (url: string) => {
      if (url === '/collaboration/summary') return { data: { unreadCount: 7, latestId: 12, revision: '12:7' } } as never
      if (url === '/outsource/send-queue') return { data: { count: 4 } } as never
      return { data: [] } as never
    })

    const { rerender } = render(
      <MemoryRouter initialEntries={['/oem/transfers']}>
        <AppRail active="workOrder" variant="rail" />
      </MemoryRouter>,
    )
    const collabRailBtn = await screen.findByRole('button', { name: 'Robot仿真协作' })
    expect(await within(collabRailBtn).findByText('7')).toBeInTheDocument()
    expect(within(screen.getByRole('button', { name: 'ODM/OEM工令协作' })).getByText('4')).toBeInTheDocument()

    rerender(
      <MemoryRouter initialEntries={['/oem/transfers']}>
        <AppRail active="workOrder" variant="drawer" />
      </MemoryRouter>,
    )
    const nav = screen.getByRole('navigation', { name: '业务切换' })
    expect(nav).toHaveClass('app-rail--drawer')
    expect(within(nav).getAllByRole('button').map((button) => button.getAttribute('aria-label'))).toEqual([
      'Robot仿真协作',
      'ODM/OEM工令协作',
    ])
    expect(within(within(nav).getByRole('button', { name: 'Robot仿真协作' })).getByText('7')).toBeInTheDocument()
    expect(within(within(nav).getByRole('button', { name: 'ODM/OEM工令协作' })).getByText('4')).toBeInTheDocument()
  })

  it('AdminLayout 移动导航抽屉同步 aria-expanded 并在 Escape 关闭后恢复焦点', async () => {
    const user = userEvent.setup()
    vi.spyOn(http, 'get').mockImplementation(async (url: string) => {
      if (url === '/collaboration/summary') return { data: { unreadCount: 0, latestId: 0, revision: '0' } } as never
      if (url === '/outsource/send-queue') return { data: { count: 0 } } as never
      return { data: [] } as never
    })

    render(
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route element={<AdminLayout />}>
            <Route index element={<div>工作台主体</div>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    )

    const trigger = screen.getByRole('button', { name: '打开导航菜单' })
    expect(trigger).toHaveAttribute('aria-expanded', 'false')

    await user.click(trigger)
    const drawer = await waitFor(() => {
      const element = document.querySelector<HTMLElement>('.mobile-nav-drawer')
      expect(element).not.toBeNull()
      return element!
    })
    expect(trigger).toHaveAttribute('aria-expanded', 'true')

    fireEvent.keyDown(drawer, { key: 'Escape', code: 'Escape', keyCode: 27, which: 27 })
    await waitFor(() => expect(document.querySelector('.mobile-nav-drawer')).toBeNull())
    await waitFor(() => expect(trigger).toHaveFocus())
    expect(trigger).toHaveAttribute('aria-expanded', 'false')
  })
})
