import { useCallback, useEffect, useState } from 'react'
import {
  Alert, Button, Card, Checkbox, Empty, Form, Input, Message, Modal, Select, Space, Switch, Tag, TreeSelect, Typography,
} from '@arco-design/web-react'
import { IconDelete, IconPlus } from '@arco-design/web-react/icon'
import type { DepartmentNode, FlowNode, FlowNodeInput, FlowTemplate, RoutingPreview } from '../../api/types'
import { useOem } from '../../OemContext'
import { useLoadEffect } from '../../useLoadEffect'

const SOURCES = [
  { value: 'SECTION_LEADER', label: '课别主管' },
  { value: 'DEPARTMENT_LEADER', label: '部门主管' },
  { value: 'DIVISION_LEADER', label: '事业部主管' },
  { value: 'SPECIFIED_USERS', label: '指定人员' },
]
const SELF_POLICIES = [
  { value: 'DESIGNATED', label: '改由备用审批人审批' },
  { value: 'SKIP', label: '本节点免审' },
  { value: 'BLOCK', label: '禁止发送' },
]

interface Person { id: number; employeeNo: string | null; realName: string | null }

function PeoplePicker({ value, onChange }: { value: Person[]; onChange: (next: Person[]) => void }) {
  const { api } = useOem()
  const [options, setOptions] = useState<Person[]>([])
  const search = useCallback(async (keyword: string) => setOptions(await api.approverOptions(keyword)), [api])
  const loadInitial = useCallback(() => search(''), [search])
  useLoadEffect(loadInitial)
  const known = [...value, ...options.filter((option) => !value.some((item) => item.id === option.id))]
  return (
    <Select mode="multiple" showSearch filterOption={false} placeholder="搜索具有 OEM 审批权限的员工" value={value.map((item) => item.id)}
      onSearch={(keyword) => void search(keyword)}
      onChange={(ids: number[]) => onChange(ids.map((id) => known.find((item) => item.id === id)!).filter(Boolean))}
      options={known.map((item) => ({ value: item.id, label: `${item.realName ?? ''}（${item.employeeNo ?? ''}）` }))} />
  )
}

interface EditableNode extends Omit<FlowNode, 'sortNo' | 'approvers' | 'fallbacks'> { key: number; approvers: Person[]; fallbacks: Person[] }

let nodeKey = 0
const blankNode = (): EditableNode => ({
  key: ++nodeKey, name: '课别主管审批', approverSource: 'SECTION_LEADER', approvalMode: 'SINGLE', selfPolicy: 'SKIP', enabled: true, approvers: [], fallbacks: [],
})

function toTree(nodes: DepartmentNode[]): { key: string; title: string; children: ReturnType<typeof toTree> }[] {
  return nodes.map((node) => ({ key: String(node.id), title: node.name, children: toTree(node.children) }))
}

function TemplateEditor({ template, onClose, onSaved }: { template: FlowTemplate | 'new'; onClose: () => void; onSaved: () => void }) {
  const { api } = useOem()
  const existing = template === 'new' ? null : template
  const [name, setName] = useState(existing?.name ?? '')
  const [isDefault, setIsDefault] = useState(existing?.isDefault ?? false)
  const [active, setActive] = useState((existing?.status ?? 'ACTIVE') === 'ACTIVE')
  const [nodes, setNodes] = useState<EditableNode[]>(existing ? existing.nodes.map((node) => ({ ...node, key: ++nodeKey })) : [blankNode()])
  const [scopes, setScopes] = useState<string[]>(existing?.scopes.map((scope) => String(scope.id)) ?? [])
  const [tree, setTree] = useState<ReturnType<typeof toTree>>([])
  const [saving, setSaving] = useState(false)

  useEffect(() => { void api.departments().then((list) => setTree(toTree(list))) }, [api])

  const patch = (key: number, value: Partial<EditableNode>) => setNodes((list) => list.map((node) => (node.key === key ? { ...node, ...value } : node)))
  const inputs = (): FlowNodeInput[] => nodes.map((node) => ({
    name: node.name, approverSource: node.approverSource,
    approvalMode: node.approverSource === 'SPECIFIED_USERS' ? node.approvalMode : 'SINGLE',
    selfPolicy: node.selfPolicy, enabled: node.enabled,
    approverUserIds: node.approverSource === 'SPECIFIED_USERS' ? node.approvers.map((item) => item.id) : [],
    fallbackUserIds: node.selfPolicy === 'DESIGNATED' ? node.fallbacks.map((item) => item.id) : [],
  }))

  const save = async () => {
    setSaving(true)
    try {
      const departmentIds = scopes.map(Number)
      if (!existing) {
        await api.createFlowTemplate({ name, isDefault, nodes: inputs(), departmentIds })
      } else {
        await api.updateFlowTemplateDefinition(existing.id, {
          name, status: active ? 'ACTIVE' : 'DISABLED', isDefault, nodes: inputs(), departmentIds, version: existing.version,
        })
      }
      Message.success('审批模板已保存，仅影响之后发送的传递单')
      onSaved()
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal title={existing ? `编辑审批模板：${existing.name}` : '新建审批模板'} visible style={{ width: 860 }} onCancel={onClose} onOk={save} confirmLoading={saving}>
      <Space direction="vertical" style={{ width: '100%' }} size={12}>
        <Space>
          <Input style={{ width: 280 }} placeholder="模板名称" value={name} onChange={setName} maxLength={64} />
          <Checkbox checked={isDefault} onChange={setIsDefault} disabled={existing?.isDefault}>默认模板</Checkbox>
          {existing && <Space><span>启用</span><Switch checked={active} onChange={setActive} disabled={existing.isDefault} /></Space>}
        </Space>
        <div>
          <Typography.Text bold>适用组织</Typography.Text>
          <TreeSelect treeData={tree} multiple allowClear value={scopes} onChange={setScopes} placeholder="不选择时仅作为默认模板使用"
            style={{ width: '100%', marginTop: 4 }} />
          <Typography.Text type="secondary">发送人所在课别、部门、事业部依次匹配，最先匹配到的模板生效；都未匹配时使用默认模板。</Typography.Text>
        </div>
        {nodes.map((node, index) => (
          <Card key={node.key} size="small" title={`节点 ${index + 1}`} extra={
            <Space>
              <Switch size="small" checked={node.enabled} onChange={(enabled) => patch(node.key, { enabled })} />
              <Button size="mini" icon={<IconDelete />} disabled={nodes.length === 1} onClick={() => setNodes((list) => list.filter((item) => item.key !== node.key))} />
            </Space>
          }>
            <Form layout="vertical">
              <Space wrap align="start">
                <Form.Item label="节点名称"><Input style={{ width: 180 }} value={node.name} onChange={(name) => patch(node.key, { name })} /></Form.Item>
                <Form.Item label="审批人来源">
                  <Select style={{ width: 150 }} value={node.approverSource} options={SOURCES}
                    onChange={(approverSource) => patch(node.key, { approverSource, approvalMode: approverSource === 'SPECIFIED_USERS' ? 'ANY' : 'SINGLE' })} />
                </Form.Item>
                {node.approverSource === 'SPECIFIED_USERS' && (
                  <Form.Item label="审批方式">
                    <Select style={{ width: 150 }} value={node.approvalMode} onChange={(approvalMode) => patch(node.key, { approvalMode })}
                      options={[{ value: 'ANY', label: '或签（任一人通过）' }, { value: 'ALL', label: '会签（全部通过）' }]} />
                  </Form.Item>
                )}
                <Form.Item label="发送人本人是审批人时">
                  <Select style={{ width: 190 }} value={node.selfPolicy} options={SELF_POLICIES} onChange={(selfPolicy) => patch(node.key, { selfPolicy })} />
                </Form.Item>
              </Space>
              {node.approverSource === 'SPECIFIED_USERS' && (
                <Form.Item label="指定审批人"><PeoplePicker value={node.approvers} onChange={(approvers) => patch(node.key, { approvers })} /></Form.Item>
              )}
              {node.selfPolicy === 'DESIGNATED' && (
                <Form.Item label="备用审批人"><PeoplePicker value={node.fallbacks} onChange={(fallbacks) => patch(node.key, { fallbacks })} /></Form.Item>
              )}
            </Form>
          </Card>
        ))}
        <Button icon={<IconPlus />} disabled={nodes.length >= 10} onClick={() => setNodes((list) => [...list, { ...blankNode(), name: '审批节点' }])}>添加节点</Button>
      </Space>
    </Modal>
  )
}

function RoutingPreviewCard() {
  const { api } = useOem()
  const [users, setUsers] = useState<{ id: number; employeeNo: string; realName: string; departmentName: string | null }[]>([])
  const [result, setResult] = useState<RoutingPreview | null>(null)
  const search = async (keyword: string) => setUsers(await api.internalUsers(keyword))
  return (
    <Card title="审批路径预览" style={{ marginTop: 16 }}>
      <Select showSearch filterOption={false} style={{ width: 360 }} placeholder="选择一名员工，查看其发送时的审批路径"
        onSearch={(keyword) => void search(keyword)} onFocus={() => void search('')}
        onChange={async (userId: number) => setResult(await api.previewRouting(userId))}
        options={users.map((user) => ({ value: user.id, label: `${user.realName}（${user.employeeNo}）${user.departmentName ? ' · ' + user.departmentName : ''}` }))} />
      {result && (result.ok ? (
        <div style={{ marginTop: 12 }}>
          <Typography.Paragraph>
            使用模板：<b>{result.template?.name}</b>{result.matchedScope ? `（匹配组织：${result.matchedScope.name}）` : '（默认模板）'}
            {!result.requiresApproval && <Tag color="purple" style={{ marginLeft: 8 }}>无需人工审批</Tag>}
          </Typography.Paragraph>
          {result.nodes?.map((node) => (
            <div key={node.sortNo}>
              {node.sortNo}. {node.name}：{node.skipped ? '免审' : node.approvers.map((item) => item?.realName).join('、')}
              {node.usedFallback && <Tag size="small" color="orange" style={{ marginLeft: 4 }}>备用审批人</Tag>}
            </div>
          ))}
        </div>
      ) : <Alert type="warning" style={{ marginTop: 12 }} content={`该员工当前无法发送：${result.reason}`} />)}
    </Card>
  )
}

export default function FlowTemplatesPage() {
  const { api } = useOem()
  const [templates, setTemplates] = useState<FlowTemplate[]>([])
  const [editing, setEditing] = useState<FlowTemplate | 'new' | null>(null)
  const load = useCallback(async () => setTemplates(await api.flowTemplates()), [api])
  useLoadEffect(load)

  return (
    <div>
      <Card title="审批模板" extra={<Button type="primary" icon={<IconPlus />} onClick={() => setEditing('new')}>新建模板</Button>}>
        <Typography.Paragraph type="secondary">事业部、部门和课别主管统一在协作平台的“组织架构”中维护。</Typography.Paragraph>
        {templates.length === 0 && <Empty />}
        <Space direction="vertical" style={{ width: '100%' }}>
          {templates.map((template) => (
            <Card key={template.id} size="small" hoverable onClick={() => setEditing(template)} title={
              <Space>{template.name}{template.isDefault && <Tag color="arcoblue">默认</Tag>}
                {template.status !== 'ACTIVE' && <Tag>已停用</Tag>}</Space>
            }>
              <div>节点：{template.nodes.map((node) => `${node.name}${node.enabled ? '' : '（关闭）'}`).join(' → ')}</div>
              <div>适用组织：{template.scopes.length ? template.scopes.map((scope) => scope.name).join('、') : '—'}</div>
              {template.nodes.some((node) => [...node.approvers, ...node.fallbacks].some((person) => !person.eligible)) && (
                <Alert type="warning" style={{ marginTop: 8 }} content="有审批人已停用或失去 OEM 审批权限，相关发送会失败，请调整。" />
              )}
            </Card>
          ))}
        </Space>
      </Card>
      <RoutingPreviewCard />
      {editing && <TemplateEditor template={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); void load() }} />}
    </div>
  )
}
