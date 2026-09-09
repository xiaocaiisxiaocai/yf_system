import { useState } from 'react'
import { Button, Form, Input, Message } from '@arco-design/web-react'
import { IconLock } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import http, { withAuthLock } from '../api/client'
import { useAuth } from '../store/auth'
import AuthShell from '../components/AuthShell'

export default function ChangePassword() {
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const { mustChangePassword, logout } = useAuth()
  const nav = useNavigate()

  const exitLogin = async () => {
    await withAuthLock(async () => {
    try {
      await http.post('/auth/logout')
    } catch {
      /* 即使服务端会话已失效，也要清空本地登录态 */
    }
    logout()
    })
    nav('/login')
  }

  const submit = async (v: { oldPassword: string; newPassword: string; confirm: string }) => {
    if (v.newPassword !== v.confirm) {
      Message.error('两次输入的新密码不一致')
      return
    }
    setLoading(true)
    try {
      await withAuthLock(async () => {
        await http.put('/auth/password', { oldPassword: v.oldPassword, newPassword: v.newPassword })
        logout()
      })
      Message.success('密码已修改，请重新登录')
      nav('/login')
    } catch {
      /* 拦截器已提示 */
    } finally {
      setLoading(false)
    }
  }

  return (
    <AuthShell>
        {mustChangePassword && (
          <div className="auth-notice" role="status">首次登录，请先修改初始密码</div>
        )}
        <Form className="auth-form" form={form} layout="vertical" onSubmit={submit}>
          <Form.Item field="oldPassword" rules={[{ required: true, message: '请输入原密码' }]}>
            <Input.Password size="large" prefix={<IconLock />} placeholder="请输入原密码" aria-label="原密码" autoComplete="current-password" />
          </Form.Item>
          <Form.Item
            field="newPassword"
            rules={[
              { required: true, message: '请输入新密码' },
              { match: /^.{6,20}$/, message: '密码需 6-20 位' },
            ]}
          >
            <Input.Password size="large" prefix={<IconLock />} placeholder="请输入新密码（6–20 位）" aria-label="新密码" autoComplete="new-password" />
          </Form.Item>
          <Form.Item field="confirm" rules={[{ required: true, message: '请再次输入新密码' }]}>
            <Input.Password size="large" prefix={<IconLock />} placeholder="请再次输入新密码" aria-label="确认新密码" autoComplete="new-password" />
          </Form.Item>
          <div className="auth-actions">
            <Button long onClick={mustChangePassword ? exitLogin : () => nav(-1)}>
              {mustChangePassword ? '退出登录' : '返回'}
            </Button>
            <Button type="primary" long htmlType="submit" loading={loading}>确认修改</Button>
          </div>
        </Form>
    </AuthShell>
  )
}
