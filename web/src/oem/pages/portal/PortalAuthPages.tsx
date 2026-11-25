import { useEffect, useRef, useState } from 'react'
import axios from 'axios'
import { Button, Form, Input, Message, Result, Space, Spin } from '@arco-design/web-react'
import { IconLock, IconUser } from '@arco-design/web-react/icon'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { isOemPortalPath, isSafeLoginReturnPath } from '../../../api/client'
import AuthShell from '../../../components/AuthShell'
import PasswordInput from '../../../components/PasswordInput'
import { confirmPasswordRule, passwordRule } from '../../../utils/password'
import {
  bootPortalSession, portalChangePassword, portalLogin, portalLogout, PORTAL_BASE, retryBootPortalSession,
  usePortalAuth, usePortalSessionSync,
} from '../../api/portalSession'
import { oemRequestError } from '../../api/requestError'

const OEM_BRAND = 'ODM/OEMA 文件传递'

export function PortalLoginPage() {
  usePortalSessionSync()
  const location = useLocation()
  const [form] = Form.useForm()
  const { booted, token, mustChangePassword } = usePortalAuth()
  const [loading, setLoading] = useState(false)
  const submitting = useRef(false)
  useEffect(() => { void bootPortalSession() }, [])

  const submit = async (values: { employeeNo: string; password: string }) => {
    if (submitting.current) return
    submitting.current = true
    setLoading(true)
    try {
      // A successful login stores the session; the redirect below then leaves this page.
      await portalLogin(values.employeeNo.trim(), values.password)
    } catch (error) {
      if (!axios.isAxiosError(error)) {
        Message.error(oemRequestError(error, '登录失败，请稍后重试'))
      }
    } finally {
      submitting.current = false
      setLoading(false)
    }
  }

  if (booted && token) {
    const from = (location.state as { from?: unknown } | null)?.from
    const pathOnly = typeof from === 'string' ? from.split(/[?#]/)[0] : ''
    const safeFrom = isSafeLoginReturnPath(from) && isOemPortalPath(pathOnly) && pathOnly !== `${PORTAL_BASE}/login`
      ? from
      : `${PORTAL_BASE}/transfers`
    return <Navigate to={mustChangePassword ? `${PORTAL_BASE}/change-password` : safeFrom} replace />
  }

  return (
    <AuthShell brand={OEM_BRAND} title="账号登录">
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
  const { booted, bootUnavailable, token, mustChangePassword, logout } = usePortalAuth()
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const [retryingBoot, setRetryingBoot] = useState(false)
  const submitting = useRef(false)
  useEffect(() => { void bootPortalSession() }, [])

  const exitLogin = async () => {
    if (submitting.current) return
    submitting.current = true
    setLoading(true)
    try {
      await portalLogout()
      navigate(`${PORTAL_BASE}/login`, { replace: true })
    } finally {
      submitting.current = false
      setLoading(false)
    }
  }

  const submit = async (values: { oldPassword: string; newPassword: string; confirm: string }) => {
    if (values.newPassword === values.oldPassword) {
      Message.error('新密码不能与当前密码相同')
      return
    }
    if (submitting.current) return
    submitting.current = true
    setLoading(true)
    try {
      // A password change revokes every session: sign in again with the new password.
      await portalChangePassword(values.oldPassword, values.newPassword)
      Message.success('密码已修改，请使用新密码重新登录')
      navigate(`${PORTAL_BASE}/login`, { replace: true })
    } catch (error) {
      if (!axios.isAxiosError(error)) {
        Message.error(oemRequestError(error, '修改密码失败，请稍后重试'))
      }
    } finally {
      submitting.current = false
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
  if (!token) return <Navigate to={`${PORTAL_BASE}/login`} replace />

  return (
    <AuthShell brand={OEM_BRAND} title={mustChangePassword ? '首次登录请修改密码' : '修改密码'}>
      <Form className="auth-form" form={form} layout="vertical" onSubmit={submit}>
        <Form.Item field="oldPassword" label="当前密码" rules={[{ required: true, message: '请输入当前密码' }]}>
          <PasswordInput size="large" prefix={<IconLock />} placeholder="请输入当前密码" aria-label="当前密码" autoComplete="current-password" disabled={loading} />
        </Form.Item>
        <Form.Item field="newPassword" label="新密码" rules={[{ required: true, message: '请输入新密码' }, passwordRule]}>
          <PasswordInput size="large" prefix={<IconLock />} placeholder="请输入新密码（5-20 位）" aria-label="新密码" autoComplete="new-password" disabled={loading} />
        </Form.Item>
        <Form.Item field="confirm" label="确认新密码" dependencies={['newPassword']} rules={[{ required: true, message: '请再次输入新密码' }, confirmPasswordRule(form)]}>
          <PasswordInput size="large" prefix={<IconLock />} placeholder="请再次输入新密码" aria-label="确认新密码" autoComplete="new-password" disabled={loading} />
        </Form.Item>
        <div className="auth-actions">
          <Button long disabled={loading} onClick={mustChangePassword ? () => void exitLogin() : () => navigate(-1)}>
            {mustChangePassword ? '退出登录' : '返回'}
          </Button>
          <Button type="primary" long htmlType="submit" loading={loading}>保存</Button>
        </div>
      </Form>
    </AuthShell>
  )
}
