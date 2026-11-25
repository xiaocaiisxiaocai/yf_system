import { useEffect, useRef, useState } from 'react'
import { Button, Form, Input, Message } from '@arco-design/web-react'
import { IconLock, IconUser } from '@arco-design/web-react/icon'
import { useLocation, useNavigate } from 'react-router-dom'
import axios from 'axios'
import { useAuth } from '../store/auth'
import { isAuthLoginResponse, isSafeLoginReturnPath, withAuthLock } from '../api/client'
import { requestErrorMessage } from '../api/requestError'
import AuthShell from '../components/AuthShell'
import PasswordInput from '../components/PasswordInput'

export default function Login() {
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const submitting = useRef(false)
  const setLogin = useAuth((s) => s.setLogin)
  const nav = useNavigate()
  const loc = useLocation() as { state?: { from?: string } }

  const submit = async (v: { employeeNo: string; password: string }) => {
    if (submitting.current) return
    submitting.current = true
    setLoading(true)
    try {
      const r = await withAuthLock(async () => {
        const response = await axios.post('/api/v1/auth/login', {
          employeeNo: v.employeeNo.trim(),
          password: v.password,
        }, { withCredentials: true, timeout: 30_000 })
        if (!isAuthLoginResponse(response.data)) {
          throw new Error('登录响应格式无效，请稍后重试')
        }
        setLogin(response.data)
        return response
      })
      Message.success('登录成功')
      if (r.data.mustChangePassword) {
        nav('/change-password')
      } else {
        const target = loc.state?.from
        const safeTarget = isSafeLoginReturnPath(target) ? target : '/'
        nav(safeTarget, { replace: true })
      }
    } catch (e) {
      Message.error(requestErrorMessage(e, '登录失败'))
    } finally {
      submitting.current = false
      setLoading(false)
    }
  }

  useEffect(() => {
    // 不再自动登出（避免打开登录页把其他标签踢下线）；已登录则直接进入系统
    const s = useAuth.getState()
    if (s.token && s.user) {
      nav('/', { replace: true })
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <AuthShell title="账号登录">
        <Form className="auth-form" form={form} layout="vertical" onSubmit={submit} autoComplete="on">
          <Form.Item field="employeeNo" rules={[{ required: true, message: '请输入账号' }]}>
            <Input size="large" prefix={<IconUser />} placeholder="请输入账号" aria-label="账号" autoComplete="username" autoFocus disabled={loading} />
          </Form.Item>
          <Form.Item field="password" rules={[{ required: true, message: '请输入密码' }]}>
            <PasswordInput size="large" prefix={<IconLock />} placeholder="请输入密码" aria-label="密码" autoComplete="current-password" disabled={loading} onPressEnter={() => form.submit()} />
          </Form.Item>
          <Button type="primary" size="large" long htmlType="submit" loading={loading}>
            登录
          </Button>
        </Form>
    </AuthShell>
  )
}
