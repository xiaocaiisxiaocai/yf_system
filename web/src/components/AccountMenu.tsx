import { Avatar, Dropdown, Menu } from '@arco-design/web-react'
import { IconDown, IconPoweroff, IconUser } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '../store/auth'
import http, { withAuthLock, type QuietRequestConfig } from '../api/client'
import type { ApiResponses } from '../api/types'
import { confirmUploadLeave } from './uploadLeave'

/** Internal staff account menu, shared by the collaboration and ODM/OEM headers. */
export default function AccountMenu() {
  const user = useAuth((state) => state.user)
  const logout = useAuth((state) => state.logout)
  const nav = useNavigate()

  const doLogout = () => {
    // 本地先退出。服务端登出同时认 refresh cookie（Path=/api/v1/auth，浏览器自动附带）
    // 和 Authorization。cookie 读不到，访问令牌必须在清空 store 之前留下，并显式放进请求。
    // authGeneration 用退出后的代际，避免请求拦截器把这次登出当成过期会话丢掉；
    // 若期间已经重新登录，代际再变，则不再发送，免得清掉新的 refresh cookie。
    const accessToken = useAuth.getState().token
    logout()
    const generationAfterLogout = useAuth.getState().generation
    nav('/login')
    void withAuthLock(async () => {
      if (useAuth.getState().generation !== generationAfterLogout) return
      try {
        const config: QuietRequestConfig = {
          headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
          authGeneration: generationAfterLogout,
        }
        await http.post<ApiResponses['POST /auth/logout']>('/auth/logout', null, config)
      } catch {
        /* 服务端登出失败不阻止本地退出 */
      }
    }).catch(() => {
      /* 锁失败也不影响已经完成的本地退出 */
    })
  }

  return (
    <Dropdown
      trigger="click"
      droplist={
        <Menu
          onClickMenuItem={(k) => {
            if (k === 'profile') nav('/profile')
            // 退出会清掉会话，进行中的上传无法继续：先确认，再退出。
            if (k === 'logout') confirmUploadLeave(doLogout)
          }}
        >
          <Menu.Item key="profile">
            <IconUser style={{ marginRight: 8 }} />
            个人资料
          </Menu.Item>
          <Menu.Item key="logout">
            <IconPoweroff style={{ marginRight: 8 }} />
            退出登录
          </Menu.Item>
        </Menu>
      }
    >
      <button className="layout-profile" type="button" aria-label={`账号菜单：${user?.realName || '当前用户'}`}>
        <Avatar size={30} style={{ background: 'rgb(var(--primary-6))' }}>
          {user?.realName?.slice(0, 1)}
        </Avatar>
        <span title={user?.realName}>{user?.realName}</span>
        <IconDown />
      </button>
    </Dropdown>
  )
}
