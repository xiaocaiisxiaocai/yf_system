import { useCallback, useEffect, useState } from 'react'
import { Button, Card, Checkbox, Form, Input, Message, Modal, Radio, Select, Space, Table, Typography } from '@arco-design/web-react'
import { IconPlus } from '@arco-design/web-react/icon'
import { useNavigate } from 'react-router-dom'
import { fmtSize, fmtTime } from '../../api/types'
import type { Option, RetentionTemplate, TransferSummary } from '../api/types'
import { ApprovalTag, DIRECTION_LABEL, LIFECYCLE_OPTIONS, LifecycleTag, ScanTag } from '../components/StatusTags'
import { useCan, useOem } from '../OemContext'

function CreateTransferModal({ visible, onClose }: { visible: boolean; onClose: () => void }) {
  const { api, realm, base } = useOem()
  const navigate = useNavigate()
  const [form] = Form.useForm()
  const [companies, setCompanies] = useState<Option[]>([])
  const [retention, setRetention] = useState<RetentionTemplate[]>([])
  const [saving, setSaving] = useState(false)
  const retentionId = Form.useWatch('retentionTemplateId', form) as number | undefined

  useEffect(() => {
    if (!visible) return
    form.resetFields()
    void api.retentionOptions().then(setRetention)
    if (realm === 'internal') void api.companyOptions().then(setCompanies)
  }, [visible, api, realm, form])

  const submit = async () => {
    const values = await form.validate()
    setSaving(true)
    try {
      const created = await api.createTransfer(values)
      Message.success('草稿已创建，请上传附件后发送')
      onClose()
      navigate(`${base}/transfers/${created.summary.id}`)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal title="新建文件传递单" visible={visible} onCancel={onClose} onOk={submit} confirmLoading={saving} unmountOnExit>
      <Form form={form} layout="vertical">
        <Form.Item field="title" label="标题" rules={[{ required: true, message: '请输入标题' }, { maxLength: 128 }]}>
          <Input placeholder="例如：底盘总成图纸 A 版" />
        </Form.Item>
        <Form.Item field="description" label="说明" rules={[{ maxLength: 1024 }]}>
          <Input.TextArea autoSize={{ minRows: 2, maxRows: 5 }} />
        </Form.Item>
        {realm === 'internal' && (
          <Form.Item field="oemCompanyId" label="目标 OEM 厂商" rules={[{ required: true, message: '请选择厂商' }]}>
            <Select showSearch placeholder="选择厂商" options={companies.map((company) => ({ value: company.id, label: company.name }))}
              filterOption={(input, option) => String(option?.props?.children ?? '').includes(input)} />
          </Form.Item>
        )}
        <Form.Item field="retentionTemplateId" label="删除策略" rules={[{ required: true, message: '请选择删除策略' }]}>
          <Select placeholder="选择管理员设定的删除策略" options={retention.map((item) => ({ value: item.id, label: item.name }))} />
        </Form.Item>
        {retentionId && (
          <Typography.Text type="secondary">{retention.find((item) => item.id === retentionId)?.summary}</Typography.Text>
        )}
      </Form>
    </Modal>
  )
}

export default function TransferListPage() {
  const { api, realm, base } = useOem()
  const can = useCan()
  const navigate = useNavigate()
  const [rows, setRows] = useState<TransferSummary[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [direction, setDirection] = useState('')
  const [status, setStatus] = useState<string | undefined>()
  const [keyword, setKeyword] = useState('')
  const [mine, setMine] = useState(false)
  const [loading, setLoading] = useState(false)
  const [creating, setCreating] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const result = await api.listTransfers({ page, pageSize: 20, direction: direction || undefined, status, keyword: keyword || undefined, mine })
      setRows(result.list)
      setTotal(result.total)
    } finally {
      setLoading(false)
    }
  }, [api, page, direction, status, keyword, mine])

  useEffect(() => { void load() }, [load])

  return (
    <Card
      title="文件传递单"
      extra={can.createTransfer && <Button type="primary" icon={<IconPlus />} onClick={() => setCreating(true)}>新建传递单</Button>}
    >
      <Space wrap style={{ marginBottom: 16 }}>
        {realm === 'internal' && (
          <Radio.Group type="button" value={direction} onChange={(value) => { setDirection(value); setPage(1) }}>
            <Radio value="">全部</Radio>
            <Radio value="INTERNAL_TO_OEM">公司发出</Radio>
            <Radio value="OEM_TO_INTERNAL">OEM 发来</Radio>
          </Radio.Group>
        )}
        <Select allowClear placeholder="状态" style={{ width: 140 }} value={status} options={LIFECYCLE_OPTIONS}
          onChange={(value) => { setStatus(value); setPage(1) }} />
        <Input.Search allowClear placeholder="搜索标题" style={{ width: 220 }} onSearch={(value) => { setKeyword(value); setPage(1) }} />
        <Checkbox checked={mine} onChange={(value) => { setMine(value); setPage(1) }}>只看我发送的</Checkbox>
      </Space>
      <Table
        rowKey="id"
        loading={loading}
        data={rows}
        onRow={(row) => ({ onClick: () => navigate(`${base}/transfers/${row.id}`), style: { cursor: 'pointer' } })}
        pagination={{ current: page, total, pageSize: 20, onChange: setPage }}
        columns={[
          { title: '标题', dataIndex: 'title', render: (value: string) => <Typography.Text bold>{value}</Typography.Text> },
          { title: '方向', dataIndex: 'direction', width: 120, render: (value: TransferSummary['direction']) => DIRECTION_LABEL[value] },
          { title: '厂商', dataIndex: 'companyName', width: 160 },
          { title: '发送人', width: 120, render: (_: unknown, row: TransferSummary) => row.sender.realName },
          {
            title: '状态', width: 220,
            render: (_: unknown, row: TransferSummary) => (
              <Space size={4} wrap>
                <LifecycleTag value={row.lifecycleStatus} />
                {row.direction === 'INTERNAL_TO_OEM' && row.lifecycleStatus === 'SEALED' && <ApprovalTag value={row.approvalStatus} />}
                {row.lifecycleStatus === 'SEALED' && row.scanSummary !== 'CLEAN' && <ScanTag value={row.scanSummary} />}
              </Space>
            ),
          },
          { title: '附件', width: 120, render: (_: unknown, row: TransferSummary) => `${row.fileCount} 个 / ${fmtSize(row.totalBytes)}` },
          { title: '发送时间', width: 170, render: (_: unknown, row: TransferSummary) => (row.sentAt ? fmtTime(row.sentAt) : '-') },
        ]}
      />
      <CreateTransferModal visible={creating} onClose={() => setCreating(false)} />
    </Card>
  )
}
