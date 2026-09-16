import { useRef, useState } from 'react'
import { Button, Form, Message } from '@arco-design/web-react'
import { IconLock } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import http, { withAuthLock } from '../api/client'
import { useAuth } from '../store/auth'
import AuthShell from '../components/AuthShell'
import PasswordInput from '../components/PasswordInput'
import { passwordRule } from '../utils/password'

export default function ChangePassword() {
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const submitting = useRef(false)
  const { mustChangePassword, logout } = useAuth()
  const nav = useNavigate()

  const exitLogin = async () => {
    if (submitting.current) return
    submitting.current = true
    setLoading(true)
    try {
      await withAuthLock(async () => {
        try {
          await http.post('/auth/logout')
        } catch {
          /* 即使服务端会话已失效，也要清空本地登录态 */
        }
        logout()
      })
      nav('/login')
    } finally {
      submitting.current = false
      setLoading(false)
    }
  }

  const submit = async (v: { oldPassword: string; newPassword: string; confirm: string }) => {
    if (v.newPassword !== v.confirm) {
      Message.error('两次输入的新密码不一致')
      return
    }
    if (v.newPassword === v.oldPassword) {
      Message.error('新密码不能与当前密码相同')
      return
    }
    if (submitting.current) return
    submitting.current = true
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
      submitting.current = false
      setLoading(false)
    }
  }

  return (
    <AuthShell title="修改密码">
        {mustChangePassword && (
          <div className="auth-notice" role="status">首次登录，请先修改初始密码</div>
        )}
        <Form className="auth-form" form={form} layout="vertical" onSubmit={submit}>
          <Form.Item label="原密码" field="oldPassword" rules={[{ required: true, message: '请输入原密码' }]}>
            <PasswordInput size="large" prefix={<IconLock />} placeholder="请输入原密码" aria-label="原密码" autoComplete="current-password" disabled={loading} />
          </Form.Item>
          <Form.Item
            field="newPassword"
            label="新密码"
            rules={[
              { required: true, message: '请输入新密码' },
              passwordRule,
            ]}
          >
            <PasswordInput size="large" prefix={<IconLock />} placeholder="请输入新密码（6–20 位）" aria-label="新密码" autoComplete="new-password" disabled={loading} />
          </Form.Item>
          <Form.Item label="确认新密码" field="confirm" rules={[{ required: true, message: '请再次输入新密码' }]}>
            <PasswordInput size="large" prefix={<IconLock />} placeholder="请再次输入新密码" aria-label="确认新密码" autoComplete="new-password" disabled={loading} />
          </Form.Item>
          <div className="auth-actions">
            <Button long disabled={loading} onClick={mustChangePassword ? exitLogin : () => nav(-1)}>
              {mustChangePassword ? '退出登录' : '返回'}
            </Button>
            <Button type="primary" long htmlType="submit" loading={loading}>确认修改</Button>
          </div>
        </Form>
    </AuthShell>
  )
}
