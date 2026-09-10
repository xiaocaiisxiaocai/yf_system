import { useEffect, useState } from 'react'
import { Button, Form, Input, Message } from '@arco-design/web-react'
import { IconLock, IconSafe, IconUser } from '@arco-design/web-react/icon'
import { useLocation, useNavigate } from 'react-router-dom'
import axios from 'axios'
import { useAuth } from '../store/auth'
import { withAuthLock } from '../api/client'
import AuthShell from '../components/AuthShell'
import PasswordInput from '../components/PasswordInput'

interface Captcha {
  captchaId: string
  svg: string
}

export default function Login() {
  const [form] = Form.useForm()
  const [captcha, setCaptcha] = useState<Captcha | null>(null)
  const [loading, setLoading] = useState(false)
  const setLogin = useAuth((s) => s.setLogin)
  const nav = useNavigate()
  const loc = useLocation() as { state?: { from?: string } }

  const loadCaptcha = async (silent = false) => {
    try {
      const r = await axios.get('/api/v1/auth/captcha')
      setCaptcha(r.data)
      form.setFieldValue('captchaCode', '')
    } catch {
      if (!silent) Message.error('验证码刷新失败')
    }
  }

  const submit = async (v: { employeeNo: string; password: string; captchaCode?: string }) => {
    setLoading(true)
    try {
      const body: Record<string, string> = { employeeNo: v.employeeNo.trim(), password: v.password }
      if (captcha) {
        body.captchaId = captcha.captchaId
        body.captchaCode = v.captchaCode || ''
      }
      const r = await withAuthLock(async () => {
        const response = await axios.post('/api/v1/auth/login', body, { withCredentials: true })
        setLogin(response.data)
        return response
      })
      Message.success('登录成功')
      if (r.data.mustChangePassword) {
        nav('/change-password')
      } else {
        nav(loc.state?.from || '/', { replace: true })
      }
    } catch (e) {
      if (axios.isAxiosError(e)) {
        const status = e.response?.status
        const msg = e.response?.data?.message
        if (status === 428) {
          // 需要验证码：拉取并展示
          await loadCaptcha(true)
          Message.info(msg || '请输入图形验证码')
        } else {
          Message.error(msg || '登录失败')
          if (captcha) void loadCaptcha(true)
        }
      } else {
        Message.error('网络错误')
      }
    } finally {
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
            <Input size="large" prefix={<IconUser />} placeholder="请输入工号" aria-label="工号" autoComplete="username" />
          </Form.Item>
          <Form.Item field="password" rules={[{ required: true, message: '请输入密码' }]}>
            <PasswordInput size="large" prefix={<IconLock />} placeholder="请输入密码" aria-label="密码" autoComplete="current-password" onPressEnter={() => form.submit()} />
          </Form.Item>
          {captcha && (
            <Form.Item field="captchaCode" rules={[{ required: true, message: '请输入验证码' }]}>
              <Input
                size="large"
                prefix={<IconSafe />}
                placeholder="验证码"
                aria-label="验证码"
                suffix={
                  <button className="captcha-refresh" type="button" aria-label="刷新验证码" title="点击刷新" onClick={() => void loadCaptcha()}>
                    <img src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(captcha.svg)}`} alt="验证码" />
                  </button>
                }
              />
            </Form.Item>
          )}
          <Button type="primary" size="large" long htmlType="submit" loading={loading}>
            登录
          </Button>
        </Form>
    </AuthShell>
  )
}
