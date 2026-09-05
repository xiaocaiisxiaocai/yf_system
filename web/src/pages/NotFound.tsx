import { Button, Result } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'

export default function NotFound() {
  const nav = useNavigate()
  return (
    <Result
      status="404"
      title="404"
      subTitle="页面不存在或已被移除"
      extra={
        <Button type="primary" onClick={() => nav('/', { replace: true })}>
          返回工作台
        </Button>
      }
      style={{ padding: '80px 0' }}
    />
  )
}
