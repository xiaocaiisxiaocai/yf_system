import { useEffect, useRef, useState } from 'react'
import { Button, Form, Input, Message } from '@arco-design/web-react'
import { IconLock, IconUser } from '@arco-design/web-react/icon'
import { useLocation, useNavigate } from 'react-router-dom'
import axios from 'axios'
import { useAuth } from '../store/auth'
import { withAuthLock } from '../api/client'
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
        }, { withCredentials: true })
        setLogin(response.data)
        return response
      })
      Message.success('登录成功')
      if (r.data.mustChangePassword) {
        nav('/change-password')
      } else {
        const target = loc.state?.from
        const safeTarget = typeof target === 'string' && target.startsWith('/') && !target.startsWith('//')
          && !target.includes('\\') && ![...target].some(character => character.charCodeAt(0) <= 32) ? target : '/'
        nav(safeTarget, { replace: true })
      }
    } catch (e) {
      if (axios.isAxiosError(e)) {
        const msg = e.response?.data?.message
        Message.error(msg || '登录失败')
      } else {
        Message.error('网络错误')
      }
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
    <AuthShell>
        <Form className="auth-form" form={form} layout="vertical" onSubmit={submit} autoComplete="on">
          <Form.Item field="employeeNo" rules={[{ required: true, message: '请输入工号' }]}>
            <Input size="large" prefix={<IconUser />} placeholder="请输入工号" aria-label="工号" autoComplete="username" disabled={loading} />
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
