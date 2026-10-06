import { useEffect, useState } from 'react'
import { Button, Card, Form, Input, Message, Typography } from '@arco-design/web-react'
import { useLocation, useNavigate } from 'react-router-dom'
import { bootPortalSession, portalHttp, portalLogin, PORTAL_BASE, usePortalAuth, usePortalSessionSync } from '../../api/portalSession'

const shell = { display: 'flex', minHeight: '100vh', alignItems: 'center', justifyContent: 'center', background: 'var(--color-fill-2)' } as const

export function PortalLoginPage() {
  usePortalSessionSync()
  const navigate = useNavigate()
  const location = useLocation()
  const [loading, setLoading] = useState(false)
  useEffect(() => { void bootPortalSession() }, [])

  const submit = async (values: { employeeNo: string; password: string }) => {
    setLoading(true)
    try {
      const mustChange = await portalLogin(values.employeeNo.trim(), values.password)
      const from = (location.state as { from?: string } | null)?.from
      navigate(mustChange ? `${PORTAL_BASE}/change-password` : from || `${PORTAL_BASE}/transfers`, { replace: true })
    } finally {
      setLoading(false)
    }
  }

  return (
    <div style={shell}>
      <Card style={{ width: 380 }} title={<><img src="/saa-logo.svg" alt="SAA" style={{ height: 22, marginRight: 8, verticalAlign: 'middle' }} />OEM 文件传递 · 厂商登录</>}>
        <Form layout="vertical" onSubmit={submit}>
          <Form.Item field="employeeNo" label="登录账号" rules={[{ required: true, message: '请输入登录账号' }]}><Input autoComplete="username" /></Form.Item>
          <Form.Item field="password" label="密码" rules={[{ required: true, message: '请输入密码' }]}><Input.Password autoComplete="current-password" /></Form.Item>
          <Button type="primary" htmlType="submit" long loading={loading}>登录</Button>
        </Form>
        <Typography.Paragraph type="secondary" style={{ marginTop: 12, fontSize: 12 }}>
          账号由公司管理员开通。公司内部员工请通过公司门户登录。
        </Typography.Paragraph>
      </Card>
    </div>
  )
}

export function PortalChangePasswordPage() {
  usePortalSessionSync()
  const navigate = useNavigate()
  const { token, mustChangePassword } = usePortalAuth()
  const [loading, setLoading] = useState(false)
  useEffect(() => { void bootPortalSession() }, [])
  useEffect(() => { if (usePortalAuth.getState().booted && !token) navigate(`${PORTAL_BASE}/login`, { replace: true }) }, [token, navigate])

  const submit = async (values: { oldPassword: string; newPassword: string; confirm: string }) => {
    if (values.newPassword !== values.confirm) {
      Message.error('两次输入的新密码不一致')
      return
    }
    setLoading(true)
    try {
      await portalHttp.put('/oem/auth/password', { oldPassword: values.oldPassword, newPassword: values.newPassword })
      // A password change revokes every session: sign in again with the new password.
      usePortalAuth.getState().logout()
      Message.success('密码已修改，请使用新密码重新登录')
      navigate(`${PORTAL_BASE}/login`, { replace: true })
    } finally {
      setLoading(false)
    }
  }

  return (
    <div style={shell}>
      <Card style={{ width: 380 }} title={mustChangePassword ? '首次登录请修改密码' : '修改密码'}>
        <Form layout="vertical" onSubmit={submit}>
          <Form.Item field="oldPassword" label="当前密码" rules={[{ required: true }]}><Input.Password /></Form.Item>
          <Form.Item field="newPassword" label="新密码" rules={[{ required: true }, { minLength: 6, maxLength: 20 }]}><Input.Password /></Form.Item>
          <Form.Item field="confirm" label="确认新密码" rules={[{ required: true }]}><Input.Password /></Form.Item>
          <Button type="primary" htmlType="submit" long loading={loading}>保存</Button>
        </Form>
      </Card>
    </div>
  )
}
