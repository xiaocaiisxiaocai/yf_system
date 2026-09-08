import { useCallback, useEffect, useState } from 'react'
import {
  Button, Drawer, Form, Input, Message, Modal, Popconfirm, Select, Space, Table, Tag, Timeline, Typography,
} from '@arco-design/web-react'
import { IconCheckCircle, IconCloseCircle, IconMinusCircle, IconPlus, IconClockCircle } from '@arco-design/web-react/icon'
import http from '../api/client'
import { useAuth } from '../store/auth'
import { type Round, type RoundLog, ROUND_STATUS, fmtTime } from '../api/types'
import { actionSlots } from './ActionSlots'

interface Props {
  projectId: number
  projectStatus: string
  onChanged: () => void
}

export default function RoundPanel({ projectId, projectStatus, onChanged }: Props) {
  const [rounds, setRounds] = useState<Round[]>([])
  const [loading, setLoading] = useState(true)
  const [createOpen, setCreateOpen] = useState(false)
  const [rejectTarget, setRejectTarget] = useState<Round | null>(null)
  const [history, setHistory] = useState<{ round: Round; logs: RoundLog[] } | null>(null)
  const [form] = Form.useForm()
  const [rejectForm] = Form.useForm()
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canDecide = isInternal ? hasPerm('round:confirm') : true

  const fetchRounds = useCallback(async () => {
    const r = await http.get(`/projects/${projectId}/rounds`)
    return r.data as Round[]
  }, [projectId])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setRounds(await fetchRounds())
    } finally {
      setLoading(false)
    }
  }, [fetchRounds])

  useEffect(() => {
    let active = true
    fetchRounds()
      .then((next) => {
        if (active) setRounds(next)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [fetchRounds])

  const createRound = async () => {
    const v = await form.validate().catch(() => null)
    if (!v) return
    await http.post(`/projects/${projectId}/rounds`, v)
    Message.success('轮次已创建')
    setCreateOpen(false)
    form.resetFields()
    load()
    onChanged()
  }

  const confirm = async (r: Round) => {
    await http.post(`/rounds/${r.id}/confirm`)
    Message.success(`第 ${r.roundNo} 轮已确认`)
    load()
    onChanged()
  }

  const reject = async () => {
    const v = await rejectForm.validate().catch(() => null)
    if (!v) return
    await http.post(`/rounds/${rejectTarget!.id}/reject`, { reason: v.reason })
    Message.success('已驳回')
    setRejectTarget(null)
    rejectForm.resetFields()
    load()
    onChanged()
  }

  const cancel = async (r: Round) => {
    await http.post(`/rounds/${r.id}/cancel`)
    Message.success('已撤销')
    load()
    onChanged()
  }

  const openHistory = async (r: Round) => {
    const resp = await http.get(`/rounds/${r.id}`)
    setHistory({ round: resp.data, logs: resp.data.logs || [] })
  }

  /** 当前用户是否为该轮确认方 */
  const myTurn = (r: Round) =>
    r.status === 'PENDING' &&
    canDecide &&
    ((r.confirmSide === 'COMPANY' && isInternal) || (r.confirmSide === 'SUPPLIER' && !isInternal))

  return (
    <div>
      <Space style={{ marginBottom: 12, width: '100%', justifyContent: 'space-between' }}>
        <Typography.Text type="secondary">
          共 {rounds.length} 轮；进行中项目可自由创建轮次，确认/驳回后锁定
        </Typography.Text>
        {isInternal && hasPerm('round:create') && projectStatus === 'IN_PROGRESS' && (
          <Button type="primary" icon={<IconPlus />} onClick={() => setCreateOpen(true)}>
            发起新一轮
          </Button>
        )}
      </Space>
      <Table
        rowKey="id"
        loading={loading}
        data={rounds}
        pagination={false}
        scroll={{ x: 958 }}
        columns={[
          { title: '轮次', dataIndex: 'roundNo', width: 70, align: 'center' as const, render: (v: number) => `第 ${v} 轮` },
          {
            title: '标题',
            dataIndex: 'title',
            width: 130,
            render: (v: string | undefined, r: Round) => (
              <div style={{ textAlign: 'left' }}>
                <Typography.Text ellipsis={{ showTooltip: true }} style={{ display: 'block', maxWidth: 98 }}>
                  {v || '-'}
                </Typography.Text>
                {r.status === 'REJECTED' && r.rejectReason && (
                  <Typography.Text type="secondary" ellipsis={{ showTooltip: true }} style={{ display: 'block', maxWidth: 98, fontSize: 12 }}>
                    驳回：{r.rejectReason}
                  </Typography.Text>
                )}
              </div>
            ),
          },
          {
            title: '确认方',
            dataIndex: 'confirmSide',
            width: 90,
            align: 'center' as const,
            render: (v: string) => (v === 'COMPANY' ? <Tag color="arcoblue">公司</Tag> : <Tag color="purple">供应商</Tag>),
          },
          {
            title: '状态',
            dataIndex: 'status',
            width: 80,
            align: 'center' as const,
            render: (v: string) => <Tag color={ROUND_STATUS[v]?.color}>{ROUND_STATUS[v]?.text || v}</Tag>,
          },
          {
            title: '发起',
            dataIndex: 'createdByName',
            width: 180,
            align: 'center' as const,
            render: (v: string, r: Round) => (
              <div>
                <div>{v || '-'}</div>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {fmtTime(r.createdAt)}
                </Typography.Text>
              </div>
            ),
          },
          {
            title: '确认',
            width: 180,
            align: 'center' as const,
            render: (_: unknown, r: Round) => (
              <div>
                <div>{r.decidedByName || '-'}</div>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {r.decidedAt ? fmtTime(r.decidedAt) : '-'}
                </Typography.Text>
              </div>
            ),
          },
          {
            title: '操作',
            width: 228,
            align: 'center' as const,
            render: (_: unknown, r: Round) => actionSlots([
              myTurn(r) && (
                <Popconfirm key="confirm" title={`确认通过第 ${r.roundNo} 轮？`} onOk={() => confirm(r)}>
                  <Button size="mini" type="primary" status="success" icon={<IconCheckCircle />}>
                    确认
                  </Button>
                </Popconfirm>
              ),
              myTurn(r) && (
                <Button key="reject" size="mini" status="danger" icon={<IconCloseCircle />} onClick={() => setRejectTarget(r)}>
                  驳回
                </Button>
              ),
              isInternal && hasPerm('round:cancel') && r.status === 'PENDING'
                && (r.createdBy === user?.id || hasPerm('project:view_all')) && (
                <Popconfirm key="cancel" title="撤销该轮次？关联文件将一并锁定" onOk={() => cancel(r)}>
                  <Button size="mini" type="text" icon={<IconMinusCircle />} title="撤销" />
                </Popconfirm>
              ),
              <Button key="history" size="mini" type="text" icon={<IconClockCircle />} title="历史" onClick={() => openHistory(r)} />,
            ], 'round'),
          },
        ]}
      />

      <Modal title="发起新一轮" visible={createOpen} onOk={createRound} onCancel={() => setCreateOpen(false)}>
        <Form form={form} layout="vertical">
          <Form.Item label="标题" field="title" rules={[{ required: true, message: '请输入轮次标题' }]}>
            <Input placeholder="如：首版图纸评审" maxLength={100} />
          </Form.Item>
          <Form.Item label="确认方" field="confirmSide" rules={[{ required: true, message: '请选择确认方' }]}>
            <Select placeholder="谁确认这一轮？">
              <Select.Option value="SUPPLIER">供应商确认</Select.Option>
              <Select.Option value="COMPANY">公司确认</Select.Option>
            </Select>
          </Form.Item>
          <Form.Item label="备注" field="remark">
            <Input.TextArea rows={3} maxLength={500} showWordLimit />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title={`驳回第 ${rejectTarget?.roundNo ?? ''} 轮`}
        visible={!!rejectTarget}
        onOk={reject}
        onCancel={() => setRejectTarget(null)}
        okButtonProps={{ status: 'danger' }}
      >
        <Form form={rejectForm} layout="vertical">
          <Form.Item label="驳回原因（必填）" field="reason" rules={[{ required: true, message: '请填写驳回原因' }]}>
            <Input.TextArea rows={4} maxLength={500} showWordLimit placeholder="驳回原因将随轮次历史永久保留" />
          </Form.Item>
        </Form>
      </Modal>

      <Drawer
        width={480}
        title={history ? `第 ${history.round.roundNo} 轮 · 状态历史` : ''}
        visible={!!history}
        onCancel={() => setHistory(null)}
        footer={null}
      >
        {history && (
          <Timeline>
            {history.logs.map((l) => (
              <Timeline.Item
                key={l.id}
                label={fmtTime(l.createdAt)}
                dotColor={l.toStatus === 'CONFIRMED' ? 'green' : l.toStatus === 'REJECTED' ? 'red' : l.toStatus === 'CANCELLED' ? 'gray' : 'blue'}
              >
                <div>
                  <Tag color={ROUND_STATUS[l.toStatus]?.color}>{ROUND_STATUS[l.toStatus]?.text || l.toStatus}</Tag>
                  <Typography.Text style={{ marginLeft: 8 }}>{l.operatorName}</Typography.Text>
                </div>
                {l.reason && (
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    原因：{l.reason}
                  </Typography.Text>
                )}
              </Timeline.Item>
            ))}
          </Timeline>
        )}
      </Drawer>
    </div>
  )
}
