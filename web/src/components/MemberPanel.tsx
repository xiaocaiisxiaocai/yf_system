import { useCallback, useEffect, useState } from 'react'
import { Button, List, Message, Modal, Select, Space, Tag, Typography } from '@arco-design/web-react'
import { IconUserAdd } from '@arco-design/web-react/icon'
import http from '../api/client'
import { useAuth } from '../store/auth'
import { type Member, fmtTime } from '../api/types'

interface UserOpt {
  id: number
  realName: string
  employeeNo: string
  deptName?: string | null
}

export default function MemberPanel({ projectId, supplierName }: { projectId: number; supplierName?: string }) {
  const [members, setMembers] = useState<Member[]>([])
  const [open, setOpen] = useState(false)
  const [options, setOptions] = useState<UserOpt[]>([])
  const [picked, setPicked] = useState<number[]>([])
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canManage = isInternal && hasPerm('project:member')

  const fetchMembers = useCallback(async () => {
    const r = await http.get(`/projects/${projectId}/members`)
    return r.data as Member[]
  }, [projectId])

  const load = useCallback(async () => {
    setMembers(await fetchMembers())
  }, [fetchMembers])

  useEffect(() => {
    let active = true
    fetchMembers().then((next) => {
      if (active) setMembers(next)
    })
    return () => {
      active = false
    }
  }, [fetchMembers])

  const openPicker = async () => {
    const r = await http.get('/internal-user-options')
    setOptions(r.data)
    setPicked(members.map((m) => m.userId))
    setOpen(true)
  }

  const save = async () => {
    if (!picked.includes(user!.id)) picked.push(user!.id) // 操作人保留在项目内（后端也会兜底）
    await http.put(`/projects/${projectId}/members`, { userIds: picked })
    Message.success('成员已更新')
    setOpen(false)
    load()
  }

  return (
    <div>
      <Space style={{ marginBottom: 12, width: '100%', justifyContent: 'space-between' }}>
        <Typography.Text type="secondary">
          项目内部成员可查看并协作本项目；供应商侧由「{supplierName || '关联供应商'}」的启用账号自动可见。
        </Typography.Text>
        {canManage && (
          <Button type="primary" icon={<IconUserAdd />} onClick={openPicker}>
            设置成员
          </Button>
        )}
      </Space>
      <List
        bordered
        dataSource={members}
        render={(m) => (
          <List.Item key={m.userId} extra={m.createdAt ? <Typography.Text type="secondary">加入于 {fmtTime(m.createdAt)}</Typography.Text> : null}>
            <List.Item.Meta
              title={
                <Space>
                  {m.realName}
                  <Typography.Text type="secondary">工号 {m.employeeNo}</Typography.Text>
                  {m.deptName && <Tag size="small">{m.deptName}</Tag>}
                </Space>
              }
            />
          </List.Item>
        )}
      />
      <Modal title="设置项目成员" visible={open} onOk={save} onCancel={() => setOpen(false)}>
        <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 8 }}>
          选择参与本项目的内部成员（多选）
        </Typography.Text>
        <Select
          mode="multiple"
          style={{ width: '100%' }}
          value={picked}
          onChange={(v) => setPicked(v as number[])}
          showSearch
          filterOption={(input, option) =>
            String(option?.props?.children ?? '').toLowerCase().includes(input.toLowerCase())
          }
        >
          {options.map((o) => (
            <Select.Option key={o.id} value={o.id}>
              {o.realName}（工号 {o.employeeNo}{o.deptName ? ` · ${o.deptName}` : ''}）
            </Select.Option>
          ))}
        </Select>
      </Modal>
    </div>
  )
}
