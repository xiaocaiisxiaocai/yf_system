import { useEffect, useState } from 'react'
import { Button, Form, Input, Message, Spin } from '@arco-design/web-react'
import { IconLock, IconUser } from '@arco-design/web-react/icon'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import AuthShell from '../../../components/AuthShell'
import PasswordInput from '../../../components/PasswordInput'
import { confirmPasswordRule, passwordRule } from '../../../utils/password'
import type { ApiResponses } from '../../api/types'
import { bootPortalSession, portalHttp, portalLogin, PORTAL_BASE, usePortalAuth, usePortalSessionSync } from '../../api/portalSession'

const OEM_BRAND = 'OEM 文件传递'

export function PortalLoginPage() {
  usePortalSessionSync()
  const location = useLocation()
  const [form] = Form.useForm()
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
    <AuthShell brand={OEM_BRAND} title="账号登录" description="使用 OEM 厂商账号登录文件传递门户">
      <Form className="auth-form" form={form} layout="vertical" onSubmit={submit} autoComplete="on">
        <Form.Item field="employeeNo" rules={[{ required: true, message: '请输入登录账号' }]}>
          <Input size="large" prefix={<IconUser />} placeholder="请输入登录账号" aria-label="登录账号" autoComplete="username" disabled={loading} />
        </Form.Item>
        <Form.Item field="password" rules={[{ required: true, message: '请输入密码' }]}>
          <PasswordInput size="large" prefix={<IconLock />} placeholder="请输入密码" aria-label="密码" autoComplete="current-password" disabled={loading} onPressEnter={() => form.submit()} />
        </Form.Item>
        <Button type="primary" size="large" long htmlType="submit" loading={loading}>登录</Button>
      </Form>
    </AuthShell>
  )
}

export function PortalChangePasswordPage() {
  usePortalSessionSync()
  const navigate = useNavigate()
  const { booted, token, mustChangePassword } = usePortalAuth()
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  useEffect(() => { void bootPortalSession() }, [])

  const submit = async (values: { oldPassword: string; newPassword: string; confirm: string }) => {
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
  if (!booted) {
    return (
      <main className="auth-page">
        <Spin tip="正在恢复登录状态…" />
      </main>
    )
  }
  if (!token) return <Navigate to={`${PORTAL_BASE}/login`} replace />

  return (
    <AuthShell brand={OEM_BRAND} title={mustChangePassword ? '首次登录请修改密码' : '修改密码'}>
      <Form className="auth-form" form={form} layout="vertical" onSubmit={submit}>
        <Form.Item field="oldPassword" label="当前密码" rules={[{ required: true, message: '请输入当前密码' }]}>
          <PasswordInput size="large" prefix={<IconLock />} placeholder="请输入当前密码" aria-label="当前密码" autoComplete="current-password" disabled={loading} />
        </Form.Item>
        <Form.Item field="newPassword" label="新密码" rules={[{ required: true, message: '请输入新密码' }, passwordRule]}>
          <PasswordInput size="large" prefix={<IconLock />} placeholder="请输入新密码（6-20 位）" aria-label="新密码" autoComplete="new-password" disabled={loading} />
        </Form.Item>
        <Form.Item field="confirm" label="确认新密码" dependencies={['newPassword']} rules={[{ required: true, message: '请再次输入新密码' }, confirmPasswordRule(form)]}>
          <PasswordInput size="large" prefix={<IconLock />} placeholder="请再次输入新密码" aria-label="确认新密码" autoComplete="new-password" disabled={loading} />
        </Form.Item>
        <Button type="primary" size="large" long htmlType="submit" loading={loading}>保存</Button>
      </Form>
    </AuthShell>
  )
}
