import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, List, Message, Modal, Select, Space, Spin, Tag, Typography } from '@arco-design/web-react'
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
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [loadedProjectId, setLoadedProjectId] = useState<number | null>(null)
  const [reloadKey, setReloadKey] = useState(0)
  const [open, setOpen] = useState(false)
  const [options, setOptions] = useState<UserOpt[]>([])
  const [picked, setPicked] = useState<number[]>([])
  const [pickerLoading, setPickerLoading] = useState(false)
  const membersRef = useRef<Member[]>([])
  const membersReadyRef = useRef(false)
  const membersRequestId = useRef(0)
  const pickerRequestId = useRef(0)
  const pickerPendingRef = useRef(false)
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canManage = isInternal && hasPerm('project:member')

  const fetchMembers = useCallback(async () => {
    const r = await http.get(`/projects/${projectId}/members`)
    return r.data as Member[]
  }, [projectId])

  const load = useCallback(() => {
    membersReadyRef.current = false
    membersRequestId.current += 1
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    const requestId = ++membersRequestId.current
    membersReadyRef.current = false
    fetchMembers().then((next) => {
      if (active && membersRequestId.current === requestId) {
        membersRef.current = next
        setMembers(next)
        setLoadedProjectId(projectId)
        setLoadError(false)
        membersReadyRef.current = true
      }
    }).catch(() => {
      if (active && membersRequestId.current === requestId) {
        membersReadyRef.current = false
        setLoadedProjectId(projectId)
        setLoadError(true)
      }
    }).finally(() => {
      if (active && membersRequestId.current === requestId) setLoading(false)
    })
    return () => {
      active = false
      if (membersRequestId.current === requestId) membersReadyRef.current = false
    }
  }, [fetchMembers, projectId, reloadKey])

  useEffect(() => () => {
    pickerRequestId.current += 1
    pickerPendingRef.current = false
  }, [])

  const openPicker = async () => {
    if (!membersReadyRef.current || pickerPendingRef.current) return
    const memberRequestId = membersRequestId.current
    const requestId = ++pickerRequestId.current
    pickerPendingRef.current = true
    setPickerLoading(true)
    try {
      const r = await http.get('/internal-user-options')
      if (!membersReadyRef.current || membersRequestId.current !== memberRequestId || pickerRequestId.current !== requestId) return
      setOptions(r.data)
      setPicked(membersRef.current.map((m) => m.userId))
      setOpen(true)
    } catch {
      if (pickerRequestId.current === requestId) Message.error('成员选项加载失败')
    } finally {
      if (pickerRequestId.current === requestId) {
        pickerPendingRef.current = false
        setPickerLoading(false)
      }
    }
  }

  const closePicker = () => {
    pickerRequestId.current += 1
    pickerPendingRef.current = false
    setPickerLoading(false)
    setOpen(false)
  }

  const save = async () => {
    if (!membersReadyRef.current) return
    const userIds = picked.includes(user!.id) ? picked : [...picked, user!.id] // 操作人保留在项目内（后端也会兜底）
    await http.put(`/projects/${projectId}/members`, { userIds })
    Message.success('成员已更新')
    closePicker()
    load()
  }

  const memberLoading = loading || loadedProjectId !== projectId
  const memberLoadError = loadError && loadedProjectId === projectId

  return (
    <div>
      <div className="section-heading">
        <div>
          <h2>项目成员</h2>
          <p>「{supplierName || '关联供应商'}」的启用账号可见</p>
        </div>
        {canManage && (
          <Button type="primary" icon={<IconUserAdd />} onClick={openPicker} disabled={memberLoading || memberLoadError || pickerLoading}>
            设置成员
          </Button>
        )}
      </div>
      {memberLoadError ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={load}>重试</Button>
        </div>
      ) : (
        <Spin loading={memberLoading} style={{ width: '100%' }}>
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
        </Spin>
      )}
      <Modal
        className="form-dialog"
        title="设置项目成员"
        visible={open}
        onOk={save}
        onCancel={closePicker}
        okText="保存成员"
        cancelText="取消"
      >
        <Typography.Text className="dialog-note" type="secondary">
          支持按姓名、工号或部门搜索
        </Typography.Text>
        <Select
          mode="multiple"
          style={{ width: '100%' }}
          value={picked}
          onChange={(v) => setPicked(v as number[])}
          showSearch
          placeholder="选择成员（多选）"
          renderTag={(tagProps) => {
            const member = membersRef.current.find((item) => item.userId === tagProps.value)
            const label = member
              ? `${member.realName}（工号 ${member.employeeNo}${member.deptName ? ` · ${member.deptName}` : ''}）`
              : tagProps.label ?? String(tagProps.value)
            return <Tag size="small" closable={tagProps.closable} onClose={tagProps.onClose}>{label}</Tag>
          }}
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
