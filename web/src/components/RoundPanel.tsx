import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button, Drawer, Form, Input, Message, Modal, Popconfirm, Select, Table, Tag, Timeline, Typography,
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
  targetId?: number
}

export default function RoundPanel({ projectId, projectStatus, onChanged, targetId }: Props) {
  const [rounds, setRounds] = useState<Round[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [createOpen, setCreateOpen] = useState(false)
  const [rejectTarget, setRejectTarget] = useState<Round | null>(null)
  const [history, setHistory] = useState<{ round: Round; logs: RoundLog[] } | null>(null)
  const historyRequestId = useRef(0)
  const [form] = Form.useForm()
  const [rejectForm] = Form.useForm()
  const { hasPerm, user } = useAuth()
  const isInternal = user?.userType === 'INTERNAL'
  const canDecide = isInternal ? hasPerm('round:confirm') : true

  const fetchRounds = useCallback(async () => {
    const r = await http.get(`/projects/${projectId}/rounds`)
    return r.data as Round[]
  }, [projectId])

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  useEffect(() => {
    let active = true
    fetchRounds()
      .then((next) => {
        if (active) {
          setRounds(next)
          setLoadError(false)
        }
      })
      .catch(() => {
        if (active) setLoadError(true)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [fetchRounds, reloadKey])

  useEffect(() => {
    historyRequestId.current += 1
    return () => {
      historyRequestId.current += 1
    }
  }, [projectId])

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
    const requestId = historyRequestId.current + 1
    historyRequestId.current = requestId
    const resp = await http.get(`/rounds/${r.id}`)
    if (historyRequestId.current !== requestId) return
    setHistory({ round: resp.data, logs: resp.data.logs || [] })
  }

  const closeHistory = () => {
    historyRequestId.current += 1
    setHistory(null)
  }

  /** 当前用户是否为该轮确认方 */
  const myTurn = (r: Round) =>
    r.status === 'PENDING' &&
    canDecide &&
    ((r.confirmSide === 'COMPANY' && isInternal) || (r.confirmSide === 'SUPPLIER' && !isInternal))

  return (
    <div>
      <div className="section-heading">
        <div>
          <h2>确认轮次（{rounds.length}）</h2>
        </div>
        {isInternal && hasPerm('round:create') && projectStatus === 'IN_PROGRESS' && (
          <Button type="primary" icon={<IconPlus />} onClick={() => setCreateOpen(true)}>
            发起新一轮
          </Button>
        )}
      </div>
      {loadError ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '24px 0' }}>
          <Typography.Text type="error">加载失败</Typography.Text>
          <Button size="small" onClick={() => { setLoadError(false); setLoading(true); load() }}>重试</Button>
        </div>
      ) : (
        <Table
          rowKey="id"
          loading={loading}
          data={targetId ? rounds.filter((round) => round.id === targetId) : rounds}
          pagination={false}
          scroll={{ x: 994 }}
          columns={[
          { title: '轮次', dataIndex: 'roundNo', width: 70, align: 'center' as const, render: (v: number) => `第 ${v} 轮` },
          {
            title: '标题',
            dataIndex: 'title',
            width: 130,
            render: (v: string | undefined, r: Round) => (
              <div className="table-cell-stack table-cell-stack--left">
                <Typography.Text className="table-cell-text" ellipsis={{ showTooltip: true }} style={{ width: 98 }}>
                  {v || '-'}
                </Typography.Text>
                {r.status === 'REJECTED' && r.rejectReason && (
                  <Typography.Text className="table-cell-text" type="secondary" ellipsis={{ showTooltip: true }} style={{ width: 98, fontSize: 12 }}>
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
              <div className="table-cell-stack">
                <Typography.Text className="table-cell-text" ellipsis={{ showTooltip: true }}>{v || '-'}</Typography.Text>
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
              <div className="table-cell-stack">
                <Typography.Text className="table-cell-text" ellipsis={{ showTooltip: true }}>{r.decidedByName || '-'}</Typography.Text>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {r.decidedAt ? fmtTime(r.decidedAt) : '-'}
                </Typography.Text>
              </div>
            ),
          },
          {
            title: '操作',
            width: 264,
            fixed: 'right' as const,
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
                  <Button size="mini" type="text" icon={<IconMinusCircle />}>撤销</Button>
                </Popconfirm>
              ),
              <Button key="history" size="mini" type="text" icon={<IconClockCircle />} onClick={() => openHistory(r)}>历史</Button>,
            ], 'round'),
          },
          ]}
        />
      )}

      <Modal
        className="form-dialog"
        style={{ width: 640 }}
        title="发起新一轮"
        visible={createOpen}
        onOk={createRound}
        onCancel={() => setCreateOpen(false)}
        okText="创建轮次"
        cancelText="取消"
      >
        <Form className="form-grid" form={form} layout="vertical">
          <Form.Item label="标题" field="title" rules={[{ required: true, message: '请输入轮次标题' }]}>
            <Input placeholder="如：首版图纸评审" maxLength={100} />
          </Form.Item>
          <Form.Item label="确认方" field="confirmSide" rules={[{ required: true, message: '请选择确认方' }]}>
            <Select placeholder="谁确认这一轮？">
              <Select.Option value="SUPPLIER">供应商确认</Select.Option>
              <Select.Option value="COMPANY">公司确认</Select.Option>
            </Select>
          </Form.Item>
          <Form.Item className="form-grid-full" label="备注" field="remark">
            <Input.TextArea rows={3} maxLength={500} showWordLimit placeholder="选填" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        className="form-dialog"
        title={`驳回第 ${rejectTarget?.roundNo ?? ''} 轮`}
        visible={!!rejectTarget}
        onOk={reject}
        onCancel={() => setRejectTarget(null)}
        okText="确认驳回"
        cancelText="取消"
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
        onCancel={closeHistory}
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
