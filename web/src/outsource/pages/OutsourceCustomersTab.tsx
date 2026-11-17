import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, Empty, Input, Message, Modal, Tag } from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import { IconSearch, IconSync } from '@arco-design/web-react/icon'
import http from '../../api/client'
import { fmtTime, type ApiResponses } from '../../api/types'
import { actionSlots } from '../../components/ActionSlots'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'

type Alias = ApiResponses['GET /outsource/settings/customers'][number]

/** 客户全称 → 简称 (外协清单契约 → 客户简称): synced from the IPMS 工单查询 list on demand, editable by hand. */
export default function OutsourceCustomersTab() {
  const [rows, setRows] = useState<Alias[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [syncing, setSyncing] = useState(false)
  const [editing, setEditing] = useState<Alias | null>(null)
  const [shortName, setShortName] = useState('')
  const [saving, setSaving] = useState(false)
  const sequence = useRef(0)

  const load = useCallback(async () => {
    const seq = ++sequence.current
    setLoading(true); setError(false)
    try {
      const response = await http.get<ApiResponses['GET /outsource/settings/customers']>('/outsource/settings/customers')
      if (seq === sequence.current) setRows(response.data)
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
      setRows(data.aliases)
      Message.success(`已同步 ${data.customers} 个客户：新增 ${data.added}，更新 ${data.updated}`
        + (data.keptManual ? `，保留手动设定 ${data.keptManual}` : ''))
    } catch { /* 统一请求层提示 */ } finally { setSyncing(false) }
  }

  const save = async () => {
    if (!editing || saving) return
    setSaving(true)
    try {
      const { data } = await http.put<ApiResponses['PUT /outsource/settings/customers/{id}']>(
        `/outsource/settings/customers/${editing.id}`, { shortName: shortName.trim() })
      setRows((current) => current.map((row) => row.id === data.id ? data : row))
      Message.success('客户简称已保存'); setEditing(null)
    } catch { /* 统一请求层提示，保留输入 */ } finally { setSaving(false) }
  }

  const shown = useMemo(() => {
    const text = keyword.trim().toLowerCase()
    return text ? rows.filter((row) => row.fullName.toLowerCase().includes(text) || row.shortName.toLowerCase().includes(text)) : rows
  }, [rows, keyword])

  const columns: TableColumnProps<Alias>[] = [
    { title: '客户全称', dataIndex: 'fullName', width: 320, ellipsis: true },
    { title: '客户简称', dataIndex: 'shortName', width: 200, ellipsis: true },
    {
      title: '来源', width: 90, align: 'center',
      render: (_, row) => row.manual ? <Tag color="arcoblue">手动</Tag> : <Tag>IPMS</Tag>,
    },
    { title: '更新时间', width: 150, render: (_, row) => fmtTime(row.updatedAt).slice(0, 16) },
    {
      title: '操作', width: 80, align: 'center', fixed: 'right',
      render: (_, row) => actionSlots([
        <Button key="edit" type="text" size="mini" onClick={() => { setShortName(row.shortName); setEditing(row) }}>编辑</Button>,
      ], 'outsource'),
    },
  ]

  if (error) return <LoadError message="客户简称加载失败" onRetry={load} />
  return <>
    <div className="page-toolbar responsive-toolbar outsource-settings-toolbar">
      <Input allowClear prefix={<IconSearch />} placeholder="客户全称或简称" style={{ width: 240 }} value={keyword} onChange={setKeyword} />
      <Button type="primary" icon={<IconSync />} loading={syncing} onClick={() => void sync()}>从 IPMS 同步</Button>
    </div>
    <ResizableTable<Alias> tableId="outsource-customer-aliases" className="page-table" rowKey="id" loading={loading} data={shown}
      pagination={false} noDataElement={loading ? <span /> : <Empty description="暂无客户简称" />}
      scroll={{ x: 840, y: 'var(--page-table-scroll-y)' }} columns={columns} />
    <Modal className="form-dialog" title={editing ? editing.fullName : '客户简称'} visible={editing !== null} onOk={() => void save()}
      onCancel={() => { if (!saving) setEditing(null) }} confirmLoading={saving} closable={!saving} maskClosable={!saving}
      okText="保存" cancelText="取消" unmountOnExit>
      <Input aria-label="客户简称" autoFocus maxLength={64} value={shortName} onChange={setShortName}
        placeholder="留空则恢复由 IPMS 同步" onPressEnter={() => void save()} />
    </Modal>
  </>
}
