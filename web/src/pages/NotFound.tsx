import { Button, Result } from '@arco-design/web-react'
import { useLocation, useNavigate } from 'react-router-dom'
import { isOemPortalPath } from '../api/client'

export default function NotFound() {
  const nav = useNavigate()
  const location = useLocation()
  const isPortal = isOemPortalPath(location.pathname)
  return (
    <Result
      status="404"
      title="404"
      subTitle="页面不存在或已被移除"
      extra={
        <Button type="primary" onClick={() => nav(isPortal ? '/oem-portal/transfers' : '/', { replace: true })}>
          {isPortal ? '返回文件传递' : '返回工作台'}
        </Button>
      }
      style={{ padding: '80px 0' }}
    />
  )
}
