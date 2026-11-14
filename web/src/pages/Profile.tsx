import { textLengthRule } from '../utils/textRules'
import { useRef, useState } from 'react'
import { Avatar, Button, Card, Form, Input, Message, Tag } from '@arco-design/web-react'
import { IconEmail, IconLock, IconSave } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import http, { withAuthLock } from '../api/client'
import { useAuth, type UserBrief } from '../store/auth'
import { confirmPasswordRule, passwordRule } from '../utils/password'
import PasswordInput from '../components/PasswordInput'
import type { ApiResponses } from '../api/types'


export default function Profile() {
  const [profileForm] = Form.useForm()
  const [passwordForm] = Form.useForm()
  const [profileLoading, setProfileLoading] = useState(false)
  const [passwordLoading, setPasswordLoading] = useState(false)
  const submitting = useRef(false)
  const busy = profileLoading || passwordLoading
  const { user, setUser, logout } = useAuth()
  const nav = useNavigate()
  const isSupplier = user?.userType === 'SUPPLIER'
  // 只有邮箱真的改了才能保存。
  const [emailDraft, setEmailDraft] = useState(user?.email ?? '')
  const emailChanged = emailDraft.trim() !== (user?.email ?? '')

  const saveProfile = async (values: { email: string }) => {
    if (submitting.current) return
    submitting.current = true
    setProfileLoading(true)
    try {
      const response = await http.put<ApiResponses['PUT /auth/profile']>('/auth/profile', { email: values.email })
      setUser(response.data.user as UserBrief)
      profileForm.setFieldsValue({ email: response.data.user.email })
      setEmailDraft(response.data.user.email)
      Message.success('个人资料已保存')
    } catch {
      /* 拦截器已提示 */
    } finally {
      submitting.current = false
      setProfileLoading(false)
    }
  }

  const changePassword = async (values: { oldPassword: string; newPassword: string; confirm: string }) => {
    if (submitting.current) return
    if (values.newPassword === values.oldPassword) {
      Message.error('新密码不能与当前密码相同')
      return
    }
    submitting.current = true
    setPasswordLoading(true)
    try {
      await withAuthLock(async () => {
        await http.put<ApiResponses['PUT /auth/password']>('/auth/password', {
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
      submitting.current = false
      setPasswordLoading(false)
    }
  }

  const displayName = (isSupplier ? user?.employeeNo : user?.realName) || user?.employeeNo || '-'
  return (
    <Card className="page-card profile-page">
      <h1 className="visually-hidden">个人资料</h1>

      <div className="profile-content">
        <section className="profile-identity" aria-label="账号信息">
          <Avatar className="profile-identity-avatar" size={56}>{displayName.slice(0, 1)}</Avatar>
          <div className="profile-identity-text">
            <div className="profile-identity-name">{displayName}</div>
            <div className="profile-identity-meta">
              {!isSupplier && <span>账号 {user?.employeeNo || '-'}</span>}
              <Tag size="small">{isSupplier ? '供应商人员' : '企业内部人员'}</Tag>
              {user?.isSystemAdmin && <Tag size="small" color="arcoblue">系统管理员</Tag>}
            </div>
          </div>
        </section>
        {isSupplier && (
          <div className="profile-boundary-note" role="note">
            供应商账号为共用登录账号；姓名和邮箱由供应商联络人资料维护
          </div>
        )}
        <div className={`profile-grid${isSupplier ? ' profile-grid--single' : ''}`}>
          {!isSupplier && <section className="profile-section" aria-labelledby="profile-contact-title">
            <h2 id="profile-contact-title" className="profile-section-title">联系方式</h2>
            <Form
              form={profileForm}
              layout="vertical"
              initialValues={{ email: user?.email }}
              onValuesChange={(_, values: { email?: string }) => setEmailDraft(values.email ?? '')}
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
                <Input prefix={<IconEmail />} placeholder="请输入联系邮箱" autoComplete="email" disabled={busy} />
              </Form.Item>
              <Button className="profile-submit" type="primary" htmlType="submit" icon={<IconSave />} loading={profileLoading}
                disabled={busy || !emailChanged}>
                保存资料
              </Button>
            </Form>
          </section>}

          <section className="profile-section" aria-labelledby="profile-password-title">
            <h2 id="profile-password-title" className="profile-section-title">登录密码</h2>
            <Form form={passwordForm} layout="vertical" onSubmit={changePassword}>
              <Form.Item label="当前密码" field="oldPassword" rules={[{ required: true, message: '请输入当前密码' }]}>
                <PasswordInput prefix={<IconLock />} placeholder="请输入当前密码" autoComplete="current-password" disabled={busy} />
              </Form.Item>
              <Form.Item
                label="新密码"
                field="newPassword"
                rules={[
                  { required: true, message: '请输入新密码' },
                  passwordRule,
                ]}
              >
                <PasswordInput prefix={<IconLock />} placeholder="5-20 位" autoComplete="new-password" disabled={busy} />
              </Form.Item>
              <Form.Item label="确认新密码" field="confirm" dependencies={['newPassword']} rules={[{ required: true, message: '请再次输入新密码' }, confirmPasswordRule(passwordForm)]}>
                <PasswordInput prefix={<IconLock />} placeholder="请再次输入新密码" autoComplete="new-password" disabled={busy} />
              </Form.Item>
              <Button className="profile-submit" type="primary" htmlType="submit" loading={passwordLoading} disabled={busy}>
                修改密码
              </Button>
            </Form>
          </section>
        </div>
      </div>
    </Card>
  )
}
