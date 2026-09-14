import { textLengthRule } from '../utils/textRules'
import { useState } from 'react'
import { Button, Card, Form, Input, Message, Typography } from '@arco-design/web-react'
import { IconEmail, IconLock, IconSave } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import http, { withAuthLock } from '../api/client'
import { useAuth } from '../store/auth'
import { passwordRule } from '../utils/password'
import PasswordInput from '../components/PasswordInput'

const { Text } = Typography

export default function Profile() {
  const [profileForm] = Form.useForm()
  const [passwordForm] = Form.useForm()
  const [profileLoading, setProfileLoading] = useState(false)
  const [passwordLoading, setPasswordLoading] = useState(false)
  const { user, setUser, logout } = useAuth()
  const nav = useNavigate()

  const saveProfile = async (values: { email: string }) => {
    setProfileLoading(true)
    try {
      const response = await http.put('/auth/profile', { email: values.email })
      setUser(response.data.user)
      profileForm.setFieldsValue({ email: response.data.user.email })
      Message.success('个人资料已保存')
    } catch {
      /* 拦截器已提示 */
    } finally {
      setProfileLoading(false)
    }
  }

  const changePassword = async (values: { oldPassword: string; newPassword: string; confirm: string }) => {
    if (values.newPassword !== values.confirm) {
      Message.error('两次输入的新密码不一致')
      return
    }
    if (values.newPassword === values.oldPassword) {
      Message.error('新密码不能与当前密码相同')
      return
    }
    setPasswordLoading(true)
    try {
      await withAuthLock(async () => {
        await http.put('/auth/password', {
          oldPassword: values.oldPassword,
          newPassword: values.newPassword,
        })
        logout()
      })
      Message.success('密码已修改，请重新登录')
      nav('/login')
    } catch {
      /* 拦截器已提示 */
    } finally {
      setPasswordLoading(false)
    }
  }

  return (
    <Card className="page-card profile-page">
      <div className="page-heading profile-heading">
        <div>
          <h1>个人资料</h1>
        </div>
      </div>

      <div className="profile-content">
        <div className="profile-readonly-grid" aria-label="账号信息">
          <div className="profile-readonly-item">
            <span>工号</span>
            <strong>{user?.employeeNo || '-'}</strong>
          </div>
          <div className="profile-readonly-item">
            <span>姓名</span>
            <strong>{user?.realName || '-'}</strong>
          </div>
          <div className="profile-readonly-item">
            <span>账号类型</span>
            <strong>{user?.userType === 'SUPPLIER' ? '供应商人员' : '企业内部人员'}</strong>
          </div>
        </div>
        {user?.userType === 'SUPPLIER' && (
          <div className="profile-boundary-note" role="note">
            账号信息由企业管理员维护
          </div>
        )}
        <div className="profile-grid">
          <section className="profile-section" aria-labelledby="profile-contact-title">
            <div className="profile-section-heading">
              <h2 id="profile-contact-title">联系方式</h2>
              <Text type="secondary">用于接收项目协作通知</Text>
            </div>
            <Form
              form={profileForm}
              layout="vertical"
              initialValues={{ email: user?.email }}
              onSubmit={saveProfile}
            >
              <Form.Item
                label="联系邮箱"
                field="email"
                rules={[
                  { required: true, message: '请输入邮箱' },
                  { type: 'email', message: '邮箱格式不正确' },
                  textLengthRule('联系邮箱', 128),
                ]}
              >
                <Input prefix={<IconEmail />} placeholder="请输入联系邮箱" autoComplete="email" />
              </Form.Item>
              <Button className="profile-submit" type="primary" htmlType="submit" icon={<IconSave />} loading={profileLoading}>
                保存资料
              </Button>
            </Form>
          </section>

          <section className="profile-section" aria-labelledby="profile-password-title">
            <div className="profile-section-heading">
              <h2 id="profile-password-title">登录密码</h2>
              <Text type="secondary">修改后需重新登录</Text>
            </div>
            <Form form={passwordForm} layout="vertical" onSubmit={changePassword}>
              <Form.Item label="当前密码" field="oldPassword" rules={[{ required: true, message: '请输入当前密码' }]}>
                <PasswordInput prefix={<IconLock />} autoComplete="current-password" />
              </Form.Item>
              <Form.Item
                label="新密码"
                field="newPassword"
                rules={[
                  { required: true, message: '请输入新密码' },
                  passwordRule,
                ]}
              >
                <PasswordInput prefix={<IconLock />} placeholder="6-20 位" autoComplete="new-password" />
              </Form.Item>
              <Form.Item label="确认新密码" field="confirm" rules={[{ required: true, message: '请再次输入新密码' }]}>
                <PasswordInput prefix={<IconLock />} autoComplete="new-password" />
              </Form.Item>
              <Button className="profile-submit" type="primary" htmlType="submit" loading={passwordLoading}>
                修改密码
              </Button>
            </Form>
          </section>
        </div>
      </div>
    </Card>
  )
}
