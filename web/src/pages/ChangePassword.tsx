import { useState } from 'react'
import { Button, Card, Form, Input, Message, Typography } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'
import http, { withAuthLock } from '../api/client'
import { useAuth } from '../store/auth'

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
    <div className="login-bg">
      <Card className="change-password-card" style={{ width: 420 }}>
        <div className="login-logo login-logo--compact">
          <img src="/saa-logo.svg" alt="SAA" />
        </div>
        <div className="change-password-brand">供应商协作平台</div>
        <Typography.Title heading={5} style={{ marginTop: 0 }}>
          修改密码
        </Typography.Title>
        {mustChangePassword && (
          <Typography.Text type="warning">首次登录需修改初始密码后才能继续使用系统。</Typography.Text>
        )}
        <Form form={form} layout="vertical" onSubmit={submit} style={{ marginTop: 16 }}>
          <Form.Item label="原密码" field="oldPassword" rules={[{ required: true, message: '请输入原密码' }]}>
            <Input.Password placeholder="原密码" />
          </Form.Item>
          <Form.Item
            label="新密码"
            field="newPassword"
            rules={[
              { required: true, message: '请输入新密码' },
              { match: /^.{6,20}$/, message: '密码需 6-20 位' },
            ]}
          >
            <Input.Password placeholder="6-20 位" />
          </Form.Item>
          <Form.Item label="确认新密码" field="confirm" rules={[{ required: true, message: '请再次输入新密码' }]}>
            <Input.Password placeholder="再次输入新密码" />
          </Form.Item>
          <Button type="primary" long htmlType="submit" loading={loading}>
            确认修改
          </Button>
          <Button style={{ marginTop: 8 }} long onClick={mustChangePassword ? exitLogin : () => nav(-1)}>
            {mustChangePassword ? '退出登录' : '返回'}
          </Button>
        </Form>
      </Card>
    </div>
  )
}
