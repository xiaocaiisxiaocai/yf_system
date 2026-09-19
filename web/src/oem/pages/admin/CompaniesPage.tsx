import { useCallback, useState } from 'react'
import { Button, Card, Drawer, Form, Input, Message, Modal, Popconfirm, Space, Table, Tag } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { fmtTime } from '../../../api/types'
import type { Company, VendorAccount } from '../../api/types'
import { useCan, useOem } from '../../OemContext'
import { useLoadEffect } from '../../useLoadEffect'

function AccountsDrawer({ company, onClose }: { company: Company | null; onClose: () => void }) {
  const { api } = useOem()
  const can = useCan()
  const [rows, setRows] = useState<VendorAccount[]>([])
  const [editing, setEditing] = useState<VendorAccount | 'new' | null>(null)
  const [form] = Form.useForm()

  const load = useCallback(async () => { if (company) setRows(await api.accounts(company.id)) }, [api, company])
  useLoadEffect(load)

  const save = async () => {
    const values = await form.validate()
    if (editing === 'new') await api.createAccount(company!.id, values)
    else if (editing) await api.updateAccount(editing.id, values)
    Message.success('已保存')
    setEditing(null)
    await load()
  }
  const reset = (account: VendorAccount) => {
    let password = ''
    Modal.confirm({
      title: `重置 ${account.realName} 的密码`,
      content: <Input.Password placeholder="新的初始密码（6-20 位）" onChange={(value) => { password = value }} />,
      onOk: async () => {
        await api.resetAccountPassword(account.id, password)
        Message.success('已重置，账号下次登录须修改密码，现有登录已失效')
      },
    })
  }

  return (
    <Drawer width={760} title={`${company?.name ?? ''} · 登录账号`} visible={Boolean(company)} onCancel={onClose} footer={null} unmountOnExit>
      {can.manageAccounts ? (
        <>
          <Button type="primary" icon={<IconPlus />} style={{ marginBottom: 12 }} onClick={() => { form.resetFields(); setEditing('new') }}>新增账号</Button>
          <Table rowKey="id" data={rows} pagination={false} columns={[
            { title: '登录账号', dataIndex: 'employeeNo' },
            { title: '姓名', dataIndex: 'realName' },
            { title: '邮箱', dataIndex: 'email' },
            {
              title: '状态', width: 150,
              render: (_: unknown, row: VendorAccount) => (
                <Space size={4}>
                  <Tag color={row.status === 'ACTIVE' ? 'green' : 'gray'} size="small">{row.status === 'ACTIVE' ? '启用' : '停用'}</Tag>
                  {row.mustChangePassword && <Tag size="small">待改密</Tag>}
                  {row.locked && <Tag color="red" size="small">已锁定</Tag>}
                </Space>
              ),
            },
            { title: '最近登录', width: 160, render: (_: unknown, row: VendorAccount) => (row.lastLoginAt ? fmtTime(row.lastLoginAt) : '-') },
            {
              title: '操作', width: 200,
              render: (_: unknown, row: VendorAccount) => (
                <Space size={4}>
                  <Button size="mini" onClick={() => { form.setFieldsValue(row); setEditing(row) }}>编辑</Button>
                  <Button size="mini" onClick={() => reset(row)}>重置密码</Button>
                  <Button size="mini" status={row.status === 'ACTIVE' ? 'warning' : 'success'}
                    onClick={async () => { await api.setAccountStatus(row.id, row.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE'); await load() }}>
                    {row.status === 'ACTIVE' ? '停用' : '启用'}
                  </Button>
                </Space>
              ),
            },
          ]} />
        </>
      ) : <span>当前账号没有 OEM 账号管理权限。</span>}
      <Modal title={editing === 'new' ? '新增账号' : '编辑账号'} visible={Boolean(editing)} onCancel={() => setEditing(null)} onOk={save} unmountOnExit>
        <Form form={form} layout="vertical">
          {editing === 'new' && (
            <Form.Item field="employeeNo" label="登录账号" rules={[{ required: true }, { match: /^[A-Za-z0-9_]{3,32}$/, message: '3~32 位字母/数字/下划线' }]}>
              <Input />
            </Form.Item>
          )}
          <Form.Item field="realName" label="姓名" rules={[{ required: true }, { maxLength: 64 }]}><Input /></Form.Item>
          <Form.Item field="email" label="邮箱" rules={[{ required: true, type: 'email' }]}><Input /></Form.Item>
          {editing === 'new' && (
            <Form.Item field="password" label="初始密码" rules={[{ required: true }, { minLength: 6, maxLength: 20 }]}><Input.Password /></Form.Item>
          )}
        </Form>
      </Modal>
    </Drawer>
  )
}

export default function CompaniesPage() {
  const { api } = useOem()
  const can = useCan()
  const [rows, setRows] = useState<Company[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [keyword, setKeyword] = useState('')
  const [editing, setEditing] = useState<Company | 'new' | null>(null)
  const [accountsOf, setAccountsOf] = useState<Company | null>(null)
  const [form] = Form.useForm()

  const load = useCallback(async () => {
    if (!can.manageCompanies) return
    const result = await api.companies({ page, pageSize: 20, keyword: keyword || undefined })
    setRows(result.list)
    setTotal(result.total)
  }, [api, page, keyword, can.manageCompanies])
  useLoadEffect(load)

  const save = async () => {
    const values = await form.validate()
    if (editing === 'new') await api.createCompany(values)
    else if (editing) await api.updateCompany(editing.id, values)
    Message.success('已保存')
    setEditing(null)
    await load()
  }

  return (
    <Card title="OEM 厂商与账号" extra={can.manageCompanies && (
      <Button type="primary" icon={<IconPlus />} onClick={() => { form.resetFields(); setEditing('new') }}>新增厂商</Button>
    )}>
      <Input.Search allowClear placeholder="搜索厂商名称" style={{ width: 260, marginBottom: 12 }} onSearch={(value) => { setKeyword(value); setPage(1) }} />
      <Table rowKey="id" data={rows} pagination={{ current: page, total, pageSize: 20, onChange: setPage }} columns={[
        { title: '厂商名称', dataIndex: 'name' },
        { title: '联系人', dataIndex: 'contactName', width: 120 },
        { title: '联系方式', width: 220, render: (_: unknown, row: Company) => [row.contactPhone, row.contactEmail].filter(Boolean).join(' / ') || '-' },
        { title: '账号', width: 100, render: (_: unknown, row: Company) => `${row.activeAccountCount ?? 0} / ${row.accountCount ?? 0}` },
        { title: '状态', width: 90, render: (_: unknown, row: Company) => <Tag color={row.status === 'ACTIVE' ? 'green' : 'gray'}>{row.status === 'ACTIVE' ? '启用' : '停用'}</Tag> },
        {
          title: '操作', width: 230,
          render: (_: unknown, row: Company) => (
            <Space size={4}>
              <Button size="mini" onClick={() => setAccountsOf(row)}>账号</Button>
              <Button size="mini" onClick={() => { form.setFieldsValue(row); setEditing(row) }}>编辑</Button>
              <Popconfirm title={row.status === 'ACTIVE' ? '停用后该厂商所有账号将立即退出登录，确定？' : '确定启用该厂商？'}
                onOk={async () => { await api.setCompanyStatus(row.id, row.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE'); await load() }}>
                <Button size="mini" status={row.status === 'ACTIVE' ? 'warning' : 'success'}>{row.status === 'ACTIVE' ? '停用' : '启用'}</Button>
              </Popconfirm>
            </Space>
          ),
        },
      ]} />
      <Modal title={editing === 'new' ? '新增厂商' : '编辑厂商'} visible={Boolean(editing)} onCancel={() => setEditing(null)} onOk={save} unmountOnExit>
        <Form form={form} layout="vertical">
          <Form.Item field="name" label="厂商名称" rules={[{ required: true }, { maxLength: 128 }]}><Input /></Form.Item>
          <Form.Item field="contactName" label="联系人"><Input maxLength={64} /></Form.Item>
          <Form.Item field="contactPhone" label="联系电话"><Input maxLength={32} /></Form.Item>
          <Form.Item field="contactEmail" label="联系邮箱" rules={[{ type: 'email' }]}><Input /></Form.Item>
          <Form.Item field="remark" label="备注"><Input.TextArea maxLength={512} /></Form.Item>
        </Form>
      </Modal>
      <AccountsDrawer company={accountsOf} onClose={() => setAccountsOf(null)} />
    </Card>
  )
}
