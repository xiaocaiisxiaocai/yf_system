import { useCallback, useEffect, useRef, useState } from 'react'
import { Button, Checkbox, Input, List, Message, Modal, Spin, Tag, Typography } from '@arco-design/web-react'
import { IconUserAdd } from '@arco-design/web-react/icon'
import http from '../api/client'
import { useAuth } from '../store/auth'
import { type Member, type SupplierMember, fmtTime } from '../api/types'

interface UserOpt {
  id: number
  realName: string
  employeeNo: string
  deptName?: string | null
  status?: Member['status']
  existingOnly?: boolean
}

export default function MemberPanel({ projectId, projectStatus = 'IN_PROGRESS' }: { projectId: number; supplierName?: string; projectStatus?: string }) {
  const [members, setMembers] = useState<Member[]>([])
  const [supplierMembers, setSupplierMembers] = useState<SupplierMember[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [loadedProjectId, setLoadedProjectId] = useState<number | null>(null)
  const [reloadKey, setReloadKey] = useState(0)
  const [open, setOpen] = useState(false)
  const [options, setOptions] = useState<UserOpt[]>([])
  const [picked, setPicked] = useState<number[]>([])
  const [search, setSearch] = useState('')
  const [pickerLoading, setPickerLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const membersRef = useRef<Member[]>([])
  const membersReadyRef = useRef(false)
  const membersRequestId = useRef(0)
  const pickerRequestId = useRef(0)
  const pickerPendingRef = useRef(false)
  const savingRef = useRef(false)
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canManage = isInternal && hasPerm('project:member') && ['DRAFT', 'IN_PROGRESS'].includes(projectStatus)

  const fetchMemberGroups = useCallback(async () => {
    const [companyResponse, supplierResponse] = await Promise.all([
      http.get(`/projects/${projectId}/members`),
      http.get(`/projects/${projectId}/supplier-members`),
    ])
    return {
      members: companyResponse.data as Member[],
      supplierMembers: supplierResponse.data as SupplierMember[],
    }
  }, [projectId])

  const load = useCallback(() => {
    membersReadyRef.current = false
    membersRequestId.current += 1
    membersRef.current = []
    setMembers([])
    setSupplierMembers([])
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    const requestId = ++membersRequestId.current
    membersReadyRef.current = false
    fetchMemberGroups().then(({ members: next, supplierMembers: nextSupplierMembers }) => {
      if (active && membersRequestId.current === requestId) {
        membersRef.current = next
        setMembers(next)
        setSupplierMembers(nextSupplierMembers)
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
  }, [fetchMemberGroups, projectId, reloadKey])

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
      const serverOptions = r.data as UserOpt[]
      const serverOptionIds = new Set(serverOptions.map((option) => option.id))
      const currentOptions = membersRef.current.map((member) => ({
        id: member.userId,
        employeeNo: member.employeeNo,
        realName: member.realName,
        deptName: member.deptName,
        status: member.status,
        existingOnly: !serverOptionIds.has(member.userId),
      }))
      const merged = new Map<number, UserOpt>()
      serverOptions.forEach((option) => merged.set(option.id, option))
      currentOptions.forEach((option) => merged.set(option.id, option))
      setOptions(Array.from(merged.values()))
      setPicked(membersRef.current.map((m) => m.userId))
      setSearch('')
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

  const closePicker = (force = false) => {
    if (savingRef.current && !force) return
    pickerRequestId.current += 1
    pickerPendingRef.current = false
    setPickerLoading(false)
    savingRef.current = false
    setSaving(false)
    setSearch('')
    setOpen(false)
  }

  const togglePicked = (id: number, checked: boolean) => {
    if (savingRef.current) return
    setPicked((current) => {
      const option = options.find((item) => item.id === id)
      if (checked && option?.existingOnly && !current.includes(id)) return current
      if (checked) return current.includes(id) ? current : [...current, id]
      return current.filter((value) => value !== id)
    })
  }

  const save = async () => {
    if (!membersReadyRef.current || !user || savingRef.current) return
    const disabledSelected = membersRef.current.some((member) => member.status === 'DISABLED' && picked.includes(member.userId))
    if (disabledSelected) {
      Message.error('请先取消选择已停用的公司成员')
      return
    }
    const userIds = picked.includes(user.id) ? picked : [...picked, user.id] // 操作人保留在项目内（后端也会兜底）
    savingRef.current = true
    setSaving(true)
    try {
      await http.put(`/projects/${projectId}/members`, { userIds })
      Message.success('成员已更新')
      closePicker(true)
      load()
    } catch {
      // HTTP 客户端已展示服务端错误，保留当前草稿供用户修正。
    } finally {
      savingRef.current = false
      setSaving(false)
    }
  }

  const memberLoading = loading || loadedProjectId !== projectId
  const memberLoadError = loadError && loadedProjectId === projectId
  const normalizedSearch = search.trim().toLocaleLowerCase()
  const filteredOptions = normalizedSearch
    ? options.filter((option) => [option.realName, option.employeeNo, option.deptName ?? ''].some((value) => value.toLocaleLowerCase().includes(normalizedSearch)))
    : options

  return (
    <div>
      <div className="section-heading">
        <div>
          <h2>项目成员</h2>
        </div>
        {canManage && (
          <Button type="primary" icon={<IconUserAdd />} onClick={openPicker} disabled={memberLoading || memberLoadError || pickerLoading}>
            设置公司成员
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
          <div className="member-groups">
            <section className="member-group" aria-label={`公司成员 ${members.length}`}>
              <div className="member-group-heading">
                <h3 className="member-group-title">公司成员 {members.length}</h3>
              </div>
              <List
                bordered
                dataSource={members}
                render={(member) => (
                  <List.Item
                    key={member.userId}
                    className="member-row"
                    extra={member.createdAt ? <Typography.Text className="member-row-time" type="secondary">加入于 {fmtTime(member.createdAt)}</Typography.Text> : null}
                  >
                    <List.Item.Meta
                      title={
                        <div className="member-row-info">
                          <Typography.Text>{member.realName}</Typography.Text>
                          <span className="member-row-meta">工号 {member.employeeNo}</span>
                          {member.deptName && <Tag size="small">{member.deptName}</Tag>}
                          {member.status === 'DISABLED' && <Tag size="small" color="red">已停用</Tag>}
                        </div>
                      }
                    />
                  </List.Item>
                )}
              />
            </section>
            <section className="member-group" aria-label={`供应商成员 ${supplierMembers.length}`}>
              <div className="member-group-heading">
                <h3 className="member-group-title">供应商成员 {supplierMembers.length}</h3>
                <Typography.Text className="member-group-note" type="secondary">关联供应商的启用账号自动参与</Typography.Text>
              </div>
              <List
                bordered
                dataSource={supplierMembers}
                render={(member) => (
                  <List.Item key={member.userId} className="member-row">
                    <List.Item.Meta
                      title={
                        <div className="member-row-info">
                          <Typography.Text>{member.realName}</Typography.Text>
                          <span className="member-row-meta">工号 {member.employeeNo}</span>
                        </div>
                      }
                    />
                  </List.Item>
                )}
              />
            </section>
          </div>
        </Spin>
      )}
      <Modal
        className="form-dialog member-picker-dialog"
        title="设置公司成员"
        visible={open}
        onOk={save}
        onCancel={() => closePicker()}
        closable={!saving}
        maskClosable={!saving}
        cancelButtonProps={{ disabled: saving }}
        confirmLoading={saving}
        okText="保存成员"
        cancelText="取消"
      >
        <div className="member-picker">
          <Input.Search
            className="member-picker-search"
            value={search}
            onChange={setSearch}
            allowClear
            placeholder="搜索姓名、工号或部门"
            disabled={saving}
          />
          <div className="member-picker-toolbar">
            <Typography.Text>已选 {picked.length} 人</Typography.Text>
          </div>
          <div className="member-picker-list" role="list">
            {filteredOptions.length > 0 ? filteredOptions.map((option) => (
              <Checkbox
                key={option.id}
                className="member-picker-option"
                checked={picked.includes(option.id)}
                onChange={(checked) => togglePicked(option.id, checked)}
                disabled={saving || (option.existingOnly === true && !picked.includes(option.id))}
              >
                <span className="member-picker-option-info">
                  <span>{option.realName}</span>
                  <span className="member-row-meta">工号 {option.employeeNo}{option.deptName ? ` · ${option.deptName}` : ''}</span>
                  {option.status === 'DISABLED' && <Tag size="small" color="red">已停用</Tag>}
                </span>
              </Checkbox>
            )) : (
              <Typography.Text type="secondary">没有匹配的公司成员</Typography.Text>
            )}
          </div>
        </div>
      </Modal>
    </div>
  )
}
