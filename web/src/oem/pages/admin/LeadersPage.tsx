import { useCallback, useEffect, useState } from 'react'
import { Button, Card, Message, Modal, Select, Table, Tag, Typography } from '@arco-design/web-react'
import type { DepartmentNode } from '../../api/types'
import { useOem } from '../../OemContext'

const KIND_LABEL: Record<DepartmentNode['kind'], string> = { DIVISION: '事业部', DEPARTMENT: '部门', SECTION: '课别' }

/** Organisation leaders drive OEM approval routing (section/department/division leader nodes). */
export default function LeadersPage() {
  const { api } = useOem()
  const [tree, setTree] = useState<DepartmentNode[]>([])
  const [editing, setEditing] = useState<DepartmentNode | null>(null)
  const [candidates, setCandidates] = useState<{ id: number; employeeNo: string; realName: string; canApprove: boolean; departmentName: string | null }[]>([])
  const [selected, setSelected] = useState<number | undefined>()

  const load = useCallback(async () => setTree(await api.departments()), [api])
  useEffect(() => { void load() }, [load])

  const search = async (keyword: string) => setCandidates(await api.internalUsers(keyword))
  const save = async (leaderId: number | null) => {
    await api.setLeader(editing!.id, leaderId)
    Message.success('已更新，仅影响之后发送的传递单')
    setEditing(null)
    await load()
  }

  return (
    <Card title="组织主管">
      <Typography.Paragraph type="secondary">
        课别、部门、事业部主管用于 OEM 公司出站审批。主管需要具备 OEM 审批权限；已发送的传递单保留发送时解析出的审批人，不受此处修改影响。
      </Typography.Paragraph>
      <Table rowKey="id" data={tree} pagination={false} defaultExpandAllRows columns={[
        { title: '组织', dataIndex: 'name' },
        { title: '层级', width: 90, render: (_: unknown, row: DepartmentNode) => KIND_LABEL[row.kind] },
        {
          title: '主管', width: 260,
          render: (_: unknown, row: DepartmentNode) => row.leader
            ? <>{row.leader.realName}（{row.leader.employeeNo}）{!row.leader.active && <Tag color="red" size="small">已停用</Tag>}</>
            : <Typography.Text type="secondary">未设置</Typography.Text>,
        },
        {
          title: '操作', width: 120,
          render: (_: unknown, row: DepartmentNode) => (
            <Button size="mini" onClick={() => { setEditing(row); setSelected(row.leader?.id); void search('') }}>设置主管</Button>
          ),
        },
      ]} />
      <Modal title={`设置主管：${editing?.name ?? ''}`} visible={Boolean(editing)} onCancel={() => setEditing(null)} unmountOnExit
        footer={[
          <Button key="clear" status="warning" onClick={() => void save(null)}>清空主管</Button>,
          <Button key="ok" type="primary" disabled={!selected} onClick={() => void save(selected ?? null)}>保存</Button>,
        ]}>
        <Select showSearch filterOption={false} style={{ width: '100%' }} value={selected} onChange={setSelected}
          onSearch={(keyword) => void search(keyword)} placeholder="搜索内部员工"
          options={candidates.map((user) => ({
            value: user.id,
            label: `${user.realName}（${user.employeeNo}）${user.departmentName ? ' · ' + user.departmentName : ''}${user.canApprove ? '' : ' · 无 OEM 审批权限'}`,
          }))} />
      </Modal>
    </Card>
  )
}
