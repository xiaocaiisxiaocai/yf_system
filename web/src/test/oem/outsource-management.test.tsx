import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation, useNavigate } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from '../../App'
import http from '../../api/client'
import { useAuth } from '../../store/auth'

const settingsPath = '/oem/admin/outsource-settings'
const bothPermissions = ['oem:directory_view', 'outsource:settings']

function RouterHistory() {
  const { pathname, search } = useLocation()
  const navigate = useNavigate()
  return <>
    <output aria-label="当前地址">{pathname}{search}</output>
    <button onClick={() => navigate(-1)}>后退</button>
  </>
}

function openRoute(permissions: string[], entries: string[]) {
  useAuth.setState({
    token: 'internal-test-token', permissions, menus: [], booted: true, mustChangePassword: false,
    user: {
      id: 1, employeeNo: 'E001', realName: '设定测试', email: 'settings@example.test',
      userType: 'INTERNAL', supplierId: null, isSystemAdmin: false,
    },
  })
  const get = vi.spyOn(http, 'get').mockImplementation(async (url) => {
    if (url === '/oem/companies') return { data: { list: [], total: 0, page: 1, pageSize: 20 } }
    if (url === '/outsource/settings/vendors' || url === '/outsource/settings/types') return { data: [] }
    throw new Error(`未处理的测试请求 ${url}`)
  })
  render(<MemoryRouter initialEntries={entries}><App /><RouterHistory /></MemoryRouter>)
  return get
}

describe('外协管理设定路由', () => {
  afterEach(() => { vi.restoreAllMocks() })

  it.each(['oem:company_manage', 'oem:account_manage', 'oem:directory_view'])(
    '仅有 %s 权限时回退到厂商目录，不请求外发类型或厂商映射',
    async (permission) => {
      const get = openRoute([permission], [`${settingsPath}?tab=types`])
      expect(await screen.findByRole('tab', { name: '厂商与账号', selected: true })).toBeVisible()
      expect(screen.queryByRole('tab', { name: '外发类型' })).not.toBeInTheDocument()
      await waitFor(() => expect(get.mock.calls.map(([url]) => url)).toContain('/oem/companies'))
      expect(get.mock.calls.map(([url]) => url)).not.toContain('/outsource/settings/types')
      expect(get.mock.calls.map(([url]) => url)).not.toContain('/outsource/settings/vendors')
      expect(screen.getByLabelText('当前地址').textContent).toBe(`${settingsPath}?tab=companies`)
    },
  )

  it('只有外发设定权限时，厂商目录书签回退到外发类型', async () => {
    const get = openRoute(['outsource:settings'], ['/oem/admin/companies'])
    expect(await screen.findByRole('tab', { name: '外发类型', selected: true })).toBeVisible()
    expect(screen.queryByRole('tab', { name: '厂商与账号' })).not.toBeInTheDocument()
    await waitFor(() => expect(get.mock.calls.map(([url]) => url)).toEqual(
      expect.arrayContaining(['/outsource/settings/types', '/outsource/settings/vendors']),
    ))
    expect(get.mock.calls.map(([url]) => url)).not.toContain('/oem/companies')
    expect(screen.getByLabelText('当前地址').textContent).toBe(`${settingsPath}?tab=types`)
  })

  it.each(['', '?tab=unknown'])('双权限遇到缺失或无效分区（%s）时默认显示厂商与账号', async (query) => {
    openRoute(bothPermissions, [settingsPath + query])
    expect(await screen.findByRole('tab', { name: '厂商与账号', selected: true })).toBeVisible()
    expect(screen.getByRole('tab', { name: '外发类型', selected: false })).toBeVisible()
    expect(screen.getByRole('heading', { name: '外协管理设定', level: 1 })).toHaveClass('visually-hidden')
    await waitFor(() => expect(screen.getByLabelText('当前地址').textContent).toBe(`${settingsPath}?tab=companies`))
  })

  it('按网址打开外发类型，切换分区后同步网址并保留其他参数', async () => {
    const user = userEvent.setup()
    openRoute(bothPermissions, [`${settingsPath}?tab=types&ref=bookmark`])
    expect(await screen.findByRole('tab', { name: '外发类型', selected: true })).toBeVisible()
    await user.click(screen.getByRole('tab', { name: '厂商与账号' }))
    expect(await screen.findByRole('tab', { name: '厂商与账号', selected: true })).toBeVisible()
    const address = new URL(screen.getByLabelText('当前地址').textContent!, 'http://localhost')
    expect(address.pathname).toBe(settingsPath)
    expect(address.searchParams.get('tab')).toBe('companies')
    expect(address.searchParams.get('ref')).toBe('bookmark')
  })

  it('旧厂商网址替换为厂商分区，后退可回到此前的页面', async () => {
    const user = userEvent.setup()
    openRoute(bothPermissions, [`${settingsPath}?tab=types`, '/oem/admin/companies'])
    expect(await screen.findByRole('tab', { name: '厂商与账号', selected: true })).toBeVisible()
    expect(screen.getByLabelText('当前地址').textContent).toBe(`${settingsPath}?tab=companies`)
    await user.click(screen.getByRole('button', { name: '后退' }))
    expect(await screen.findByRole('tab', { name: '外发类型', selected: true })).toBeVisible()
    expect(screen.getByLabelText('当前地址').textContent).toBe(`${settingsPath}?tab=types`)
  })

  it('缺少四项授权时拒绝直接访问，不加载任一分区的数据', async () => {
    const get = openRoute(['oem:audit_view'], [`${settingsPath}?tab=types`])
    expect(await screen.findByText('无操作权限')).toBeVisible()
    expect(screen.queryByRole('tab')).not.toBeInTheDocument()
    expect(get).not.toHaveBeenCalled()
  })
})
