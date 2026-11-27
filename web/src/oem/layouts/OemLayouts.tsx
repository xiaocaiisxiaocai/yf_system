import { useEffect, useMemo, useState } from 'react'
import { Avatar, Button, Dropdown, Menu, Result, Space, Spin } from '@arco-design/web-react'
import {
  IconDown, IconFile, IconList, IconLock, IconPoweroff,
} from '@arco-design/web-react/icon'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { QueryClientProvider } from '@tanstack/react-query'
import { useAuth } from '../../store/auth'
import { OemApi } from '../api/OemApi'
import {
  bootPortalSession, createPortalQueryScope, portalHttp, portalLogout, portalQueryClient, PORTAL_BASE,
  retryBootPortalSession, usePortalAuth, usePortalSessionSync,
} from '../api/portalSession'
import { OemProvider } from '../OemContext'
import { useInternalOem } from '../useInternalOem'
import Shell from '../../layouts/SectionShell'
import WorkOrderLayout, { WorkOrderUnavailable } from '../../layouts/WorkOrderLayout'
import { workOrderHome, WORK_ORDER_TITLE } from '../../layouts/navigation'
import { confirmUploadLeave } from '../../components/uploadLeave'
import './OemLayouts.css'

export function OemLanding() {
  const home = useAuth(workOrderHome)
  return home ? <Navigate to={home} replace /> : <WorkOrderUnavailable />
}

export function OemInternalLayout() {
  const oemValue = useInternalOem()
  return (
    <OemProvider value={oemValue}>
      <WorkOrderLayout />
    </OemProvider>
  )
}

const portalApi = new OemApi(portalHttp)

/** Vendor portal: OEM accounts only; never loads collaboration routes or menus. */
export function PortalLayout() {
  const { booted, bootUnavailable, token, account, mustChangePassword, generation, logout } = usePortalAuth()
  usePortalSessionSync()
  const navigate = useNavigate()
  const location = useLocation()
  const [retryingBoot, setRetryingBoot] = useState(false)
  useEffect(() => { void bootPortalSession() }, [])
  const permissions = useMemo(() => new Set<string>(), [])
  const queryScope = useMemo(
    () => ['portal', ...createPortalQueryScope({ account, generation })],
    [account, generation],
  )
  const oemValue = useMemo(() => ({
    api: portalApi,
    realm: 'oem' as const,
    base: PORTAL_BASE,
    permissions,
    userId: account?.id ?? null,
    queryScope,
  }), [permissions, account?.id, queryScope])
  if (!booted) {
    return (
      <div className="app-boot">
        <img src="/saa-logo.svg" alt="SAA" />
        <Spin tip="正在恢复登录状态…" />
      </div>
    )
  }
  if (!token && bootUnavailable) {
    return (
      <div className="app-boot">
        <img src="/saa-logo.svg" alt="SAA" />
        <Result
          status="warning"
          title="暂时无法恢复登录状态"
          subTitle="当前网络连接异常或服务暂时不可用，已保留本地会话。您可以重试恢复，或重新登录。"
          extra={(
            <Space>
              <Button
                type="primary"
                loading={retryingBoot}
                onClick={async () => {
                  if (retryingBoot) return
                  setRetryingBoot(true)
                  try {
                    await retryBootPortalSession()
                  } finally {
                    setRetryingBoot(false)
                  }
                }}
              >
                重试恢复会话
              </Button>
              <Button disabled={retryingBoot} onClick={() => logout()}>重新登录</Button>
            </Space>
          )}
        />
      </div>
    )
  }
  if (!token) return <Navigate to={`${PORTAL_BASE}/login`} state={{ from: `${location.pathname}${location.search}` }} replace />
  if (mustChangePassword) return <Navigate to={`${PORTAL_BASE}/change-password`} replace />
  return (
    <QueryClientProvider client={portalQueryClient}>
      <OemProvider value={oemValue}>
        <Shell title={WORK_ORDER_TITLE} subtitle={account?.companyName} menu={[
          { path: `${PORTAL_BASE}/outsource`, label: '外发工令', icon: <IconList />, visible: true },
          { path: `${PORTAL_BASE}/transfers`, label: '文件传递单', icon: <IconFile />, visible: true },
        ]} account={
          <Dropdown trigger="click" droplist={
            <Menu onClickMenuItem={(key) => {
              if (key === 'password') navigate(`${PORTAL_BASE}/change-password`)
              if (key === 'logout') confirmUploadLeave(() => void portalLogout())
            }}>
              <Menu.Item key="password"><IconLock style={{ marginRight: 8 }} />修改密码</Menu.Item>
              <Menu.Item key="logout"><IconPoweroff style={{ marginRight: 8 }} />退出登录</Menu.Item>
            </Menu>
          }>
            <Button className="oem-account-button" type="text" style={{ color: 'inherit' }} title={account?.employeeNo}>
              <span aria-hidden="true" style={{ display: 'inline-flex', flexShrink: 0 }}>
                <Avatar size={30} style={{ background: 'rgb(var(--primary-6))' }}>
                  {account?.employeeNo?.slice(0, 1)}
                </Avatar>
              </span>
              <span className="oem-account-name">{account?.employeeNo}</span>
              <IconDown />
            </Button>
          </Dropdown>
        } />
      </OemProvider>
    </QueryClientProvider>
  )
}
