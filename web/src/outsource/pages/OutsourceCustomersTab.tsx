import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, Empty, Form, Input, Message, Modal, Radio, Space, Tag } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconPlus, IconSearch, IconSync } from '@arco-design/web-react/icon'
import { DEFAULT_PAGE_SIZE } from '../../components/tablePagination'
import http from '../../api/client'
import { fmtTime, type ApiResponses } from '../../api/types'
import { actionSlots } from '../../components/ActionSlots'
import DangerPopconfirm from '../../components/DangerPopconfirm'
import EnterSubmit from '../../components/EnterSubmit'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'

type Alias = ApiResponses['GET /outsource/settings/customers'][number]
type Missing = ApiResponses['GET /outsource/settings/customers/missing'][number]
type View = 'aliases' | 'missing'
/** The dialog edits an existing pair (id) or adds one, possibly for a missing customer (full name filled in). */
type Draft = { id?: number; fullName: string; shortName: string }

/** 客户全称 → 简称 (外协清单契约 → 客户简称): synced from the IPMS 工单查询 list on demand, added or edited by hand. */
export default function OutsourceCustomersTab() {
  const [rows, setRows] = useState<Alias[]>([])
  const [missing, setMissing] = useState<Missing[]>([])
  const [view, setView] = useState<View>('aliases')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [syncing, setSyncing] = useState(false)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [saving, setSaving] = useState(false)
  const sequence = useRef(0)

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const [aliasResponse, missingResponse] = await Promise.all([
        http.get<ApiResponses['GET /outsource/settings/customers']>('/outsource/settings/customers'),
        http.get<ApiResponses['GET /outsource/settings/customers/missing']>('/outsource/settings/customers/missing'),
      ])
      if (seq === sequence.current) { setRows(aliasResponse.data); setMissing(missingResponse.data) }
    } catch {
      if (seq === sequence.current) setError(true)
    } finally {
      if (seq === sequence.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; sequence.current += 1 }
  }, [load])

  const sync = async () => {
    if (syncing) return
    setSyncing(true)
    try {
      const { data } = await http.post<ApiResponses['POST /outsource/settings/customers/sync']>('/outsource/settings/customers/sync')
      Message.success(`已同步 ${data.customers} 个客户：新增 ${data.added}，更新 ${data.updated}`
        + (data.keptManual ? `，保留手动设定 ${data.keptManual}` : ''))
      await load()
    } catch { /* 统一请求层提示 */ } finally { setSyncing(false) }
  }

  const save = async () => {
    if (!draft || saving) return
    if (!draft.id && (!draft.fullName.trim() || !draft.shortName.trim())) { Message.warning('请填写客户全称和简称'); return }
    setSaving(true)
    try {
      if (draft.id) {
        await http.put<ApiResponses['PUT /outsource/settings/customers/{id}']>(
          `/outsource/settings/customers/${draft.id}`, { shortName: draft.shortName.trim() })
      } else {
        await http.post<ApiResponses['POST /outsource/settings/customers']>('/outsource/settings/customers',
          { fullName: draft.fullName.trim(), shortName: draft.shortName.trim() })
      }
      Message.success('客户简称已保存'); setDraft(null); await load()
    } catch { /* 统一请求层提示，保留输入 */ } finally { setSaving(false) }
  }

  const remove = async (row: Alias) => {
    try {
      await http.delete<ApiResponses['DELETE /outsource/settings/customers/{id}']>(`/outsource/settings/customers/${row.id}`)
      Message.success('已删除'); await load()
    } catch { /* 统一请求层提示 */ }
  }

  const text = keyword.trim().toLowerCase()
  const shownAliases = useMemo(() => text
    ? rows.filter((row) => row.fullName.toLowerCase().includes(text) || row.shortName.toLowerCase().includes(text)) : rows, [rows, text])
  const shownMissing = useMemo(() => text ? missing.filter((row) => row.fullName.toLowerCase().includes(text)) : missing, [missing, text])

  const aliasColumns: TableColumnProps<Alias>[] = [
    { title: '客户全称', dataIndex: 'fullName', width: 320, align: 'left', ellipsis: true },
    { title: '客户简称', dataIndex: 'shortName', width: 200, align: 'left', ellipsis: true },
    {
      title: '来源', width: 90, align: 'center',
      render: (_, row) => row.manual ? <Tag color="arcoblue">手动</Tag> : <Tag>IPMS</Tag>,
    },
    { title: '更新时间', width: 150, render: (_, row) => fmtTime(row.updatedAt).slice(0, 16) },
    {
      title: '操作', width: 120, align: 'center', fixed: 'right',
      render: (_, row) => actionSlots([
        <Button key="edit" type="text" size="mini" onClick={() => setDraft({ id: row.id, fullName: row.fullName, shortName: row.shortName })}>编辑</Button>,
        <DangerPopconfirm key="delete" title="删除这条客户简称？" disabled={!row.manual} onOk={() => remove(row)}>
          <Button type="text" size="mini" status="danger" disabled={!row.manual}>删除</Button>
        </DangerPopconfirm>,
      ], 'single'),
    },
  ]
  const missingColumns: TableColumnProps<Missing>[] = [
    { title: '客户全称', dataIndex: 'fullName', width: 360, align: 'left', ellipsis: true },
    { title: 'IPMS 资料工单', dataIndex: 'mesWorkOrders', width: 130, align: 'right' },
    { title: '外协记录', dataIndex: 'outsourceItems', width: 110, align: 'right' },
    {
      title: '操作', width: 110, align: 'center', fixed: 'right',
      render: (_, row) => actionSlots([
        <Button key="add" type="text" size="mini" onClick={() => setDraft({ fullName: row.fullName, shortName: '' })}>设置简称</Button>,
      ], 'outsource-customer'),
    },
  ]

  const pagination = {
    current: page,
    pageSize,
    onChange: (next: number, size: number) => {
      setPage(size === pageSize ? next : 1)
      setPageSize(size)
    },
  }

  if (error) return <LoadError message="客户简称加载失败" onRetry={load} />
  return <>
    <div className="page-toolbar responsive-toolbar outsource-settings-toolbar">
      <Space wrap>
        <Radio.Group type="button" value={view} onChange={(value) => { setView(value as View); setPage(1) }}
          options={[{ label: `已有简称 ${rows.length}`, value: 'aliases' }, { label: `缺少简称 ${missing.length}`, value: 'missing' }]} />
        <Input allowClear prefix={<IconSearch />} placeholder="客户全称或简称" style={{ width: 240 }} value={keyword}
          onChange={(value) => { setKeyword(value); setPage(1) }} />
      </Space>
      <Space>
        <Button icon={<IconPlus />} onClick={() => setDraft({ fullName: '', shortName: '' })}>新增</Button>
        <Button type="primary" icon={<IconSync />} loading={syncing} onClick={() => void sync()}>从 IPMS 同步</Button>
      </Space>
    </div>
    {view === 'aliases'
      ? <ResizableTable<Alias> tableId="outsource-customer-aliases" className="page-table" rowKey="id" loading={loading} data={shownAliases}
          pagination={pagination} noDataElement={loading ? <span /> : <Empty description="暂无客户简称" />}
          scroll={{ x: 880, y: 'var(--page-table-scroll-y)' }} columns={aliasColumns} />
      : <ResizableTable<Missing> tableId="outsource-customer-missing" className="page-table" rowKey="fullName" loading={loading}
          data={shownMissing} pagination={pagination} noDataElement={loading ? <span /> : <Empty description="所有客户都有简称" />}
          scroll={{ x: 710, y: 'var(--page-table-scroll-y)' }} columns={missingColumns} />}
    <Modal className="form-dialog" title={draft?.id ? '编辑客户简称' : '新增客户简称'} visible={draft !== null} onOk={() => void save()}
      onCancel={() => { if (!saving) setDraft(null) }} confirmLoading={saving} closable={!saving} maskClosable={!saving} escToExit={!saving}
      cancelButtonProps={{ disabled: saving }} okText="保存" cancelText="取消" unmountOnExit>
      {draft && <Form layout="vertical" disabled={saving} onSubmit={() => void save()}>
        <Form.Item label="客户全称" required={!draft.id}>
          <Input aria-label="客户全称" autoFocus={!draft.id && !draft.fullName} maxLength={128} value={draft.fullName} disabled={Boolean(draft.id)}
            onChange={(value) => setDraft({ ...draft, fullName: value })} />
        </Form.Item>
        <Form.Item label="客户简称" required={!draft.id}>
          <Input aria-label="客户简称" autoFocus={Boolean(draft.id || draft.fullName)} maxLength={64} value={draft.shortName} onChange={(value) => setDraft({ ...draft, shortName: value })}
            placeholder={draft.id ? '留空则恢复由 IPMS 同步' : undefined} onPressEnter={() => void save()} />
        </Form.Item>
        <EnterSubmit />
      </Form>}
    </Modal>
  </>
}
