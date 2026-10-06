import { useEffect, useState } from 'react'
import { Button, Card, Form, Input, Message, Spin, Typography } from '@arco-design/web-react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { passwordRule } from '../../../utils/password'
import type { ApiResponses } from '../../api/types'
import { bootPortalSession, portalHttp, portalLogin, PORTAL_BASE, usePortalAuth, usePortalSessionSync } from '../../api/portalSession'

const shell = { display: 'flex', minHeight: '100vh', alignItems: 'center', justifyContent: 'center', background: 'var(--color-fill-2)' } as const

export function PortalLoginPage() {
  usePortalSessionSync()
  const location = useLocation()
  const { booted, token, mustChangePassword } = usePortalAuth()
  const [loading, setLoading] = useState(false)
  useEffect(() => { void bootPortalSession() }, [])

  const submit = async (values: { employeeNo: string; password: string }) => {
    setLoading(true)
    try {
      // A successful login stores the session; the redirect below then leaves this page.
      await portalLogin(values.employeeNo.trim(), values.password)
    } finally {
      setLoading(false)
    }
  }

  if (booted && token) {
    const from = (location.state as { from?: string } | null)?.from
    return <Navigate to={mustChangePassword ? `${PORTAL_BASE}/change-password` : from || `${PORTAL_BASE}/transfers`} replace />
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
  const { booted, token, mustChangePassword } = usePortalAuth()
  const [loading, setLoading] = useState(false)
  useEffect(() => { void bootPortalSession() }, [])

  const submit = async (values: { oldPassword: string; newPassword: string; confirm: string }) => {
    if (values.newPassword !== values.confirm) {
      Message.error('两次输入的新密码不一致')
      return
    }
    setLoading(true)
    try {
      await portalHttp.put<ApiResponses['PUT /oem/auth/password']>('/oem/auth/password', { oldPassword: values.oldPassword, newPassword: values.newPassword })
      // A password change revokes every session: sign in again with the new password.
      usePortalAuth.getState().logout()
      Message.success('密码已修改，请使用新密码重新登录')
      navigate(`${PORTAL_BASE}/login`, { replace: true })
    } finally {
      setLoading(false)
    }
  }

  // Bootstrap may fail to restore a session (expired refresh cookie): there is nothing to change then.
  if (!booted) return <Spin style={{ display: 'block', marginTop: 120 }} tip="正在恢复登录状态…" />
  if (!token) return <Navigate to={`${PORTAL_BASE}/login`} replace />

  return (
    <div style={shell}>
      <Card style={{ width: 380 }} title={mustChangePassword ? '首次登录请修改密码' : '修改密码'}>
        <Form layout="vertical" onSubmit={submit}>
          <Form.Item field="oldPassword" label="当前密码" rules={[{ required: true, message: '请输入当前密码' }]}><Input.Password autoComplete="current-password" /></Form.Item>
          <Form.Item field="newPassword" label="新密码" rules={[{ required: true, message: '请输入新密码' }, passwordRule]}><Input.Password autoComplete="new-password" /></Form.Item>
          <Form.Item field="confirm" label="确认新密码" rules={[{ required: true, message: '请再次输入新密码' }]}><Input.Password /></Form.Item>
          <Button type="primary" htmlType="submit" long loading={loading}>保存</Button>
        </Form>
      </Card>
    </div>
  )
}
