import { afterEach, describe, expect, it } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import {
  createPortalQueryScope,
  portalQueryClient,
  usePortalAuth,
  usePortalSessionSync,
  type PortalAccount,
} from '../../oem/api/portalSession'

const account: PortalAccount = {
  id: 31,
  employeeNo: 'OEM31',
  realName: '厂商用户',
  email: 'oem31@example.test',
  companyId: 9,
  companyName: '测试代工厂',
}

afterEach(() => {
  usePortalAuth.setState({
    token: null,
    account: null,
    mustChangePassword: false,
    booted: false,
    generation: 0,
  })
  portalQueryClient.clear()
})

describe('OEM 厂商会话缓存隔离', () => {
  it('账号切换和退出会清空门户缓存，刷新同一会话则保留缓存', () => {
    portalQueryClient.setQueryData(['oem', 'transfers'], ['old-account-data'])
    usePortalAuth.getState().setLogin('token-1', account, false)

    expect(usePortalAuth.getState().generation).toBe(1)
    expect(createPortalQueryScope(usePortalAuth.getState())).toEqual([31, 1])
    expect(portalQueryClient.getQueryData(['oem', 'transfers'])).toBeUndefined()

    portalQueryClient.setQueryData(['oem', 'transfers'], ['current-account-data'])
    usePortalAuth.getState().setLogin('token-2', account, false, false)

    expect(usePortalAuth.getState().generation).toBe(1)
    expect(portalQueryClient.getQueryData(['oem', 'transfers'])).toEqual(['current-account-data'])

    usePortalAuth.getState().logout()
    expect(usePortalAuth.getState().generation).toBe(2)
    expect(portalQueryClient.getQueryData(['oem', 'transfers'])).toBeUndefined()
  })

  it('其他标签退出后同步清除当前 OEM 门户会话', () => {
    usePortalAuth.getState().setLogin('token-1', account, false)
    renderHook(() => usePortalSessionSync())

    act(() => {
      window.dispatchEvent(new StorageEvent('storage', {
        key: 'yf-oem-portal',
        newValue: JSON.stringify({ state: { account: null }, version: 2 }),
      }))
    })

    expect(usePortalAuth.getState().token).toBeNull()
    expect(usePortalAuth.getState().account).toBeNull()
    expect(usePortalAuth.getState().generation).toBe(2)
  })
})
