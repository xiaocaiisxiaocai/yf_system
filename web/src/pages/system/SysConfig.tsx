import { useCallback, useEffect, useState } from 'react'
import { Button, Card, Descriptions, Input, InputNumber, Message, Progress, Select, Space, Table, Tag, Typography } from '@arco-design/web-react'
import http from '../../api/client'
import { fmtSize, fmtTime } from '../../api/types'

interface Cfg {
  key: string
  value: string
  description?: string
  updatedAt?: string
}

interface Storage {
  root: string
  mountPoint?: string
  totalBytes: number
  availableBytes: number
  usedPercent: number
  warnPercent: number
  warning: boolean
}

export default function SysConfig() {
  const [configs, setConfigs] = useState<Cfg[]>([])
  const [storage, setStorage] = useState<Storage | null>(null)
  const [editing, setEditing] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)

  const fetchSnapshot = useCallback(async () => {
    const [configsResponse, storageResponse] = await Promise.all([
      http.get('/admin/system/configs'),
      http.get('/admin/system/storage'),
    ])
    return { configs: configsResponse.data as Cfg[], storage: storageResponse.data as Storage }
  }, [])

  const applySnapshot = useCallback((next: { configs: Cfg[]; storage: Storage }) => {
    setConfigs(next.configs)
    setStorage(next.storage)
    setEditing({})
  }, [])

  const load = useCallback(async () => {
    applySnapshot(await fetchSnapshot())
  }, [applySnapshot, fetchSnapshot])

  useEffect(() => {
    let active = true
    fetchSnapshot().then((next) => {
      if (active) applySnapshot(next)
    })
    return () => {
      active = false
    }
  }, [applySnapshot, fetchSnapshot])

  const dirty = Object.entries(editing).filter(([k, v]) => configs.find((c) => c.key === k)?.value !== v)

  const save = async () => {
    if (dirty.length === 0) return
    setSaving(true)
    try {
      await http.put('/admin/system/configs', { items: dirty.map(([key, value]) => ({ key, value })) })
      Message.success('参数已保存')
      load()
    } catch {
      /* 拦截器已提示 */
    } finally {
      setSaving(false)
    }
  }

  const usedPct = storage && storage.totalBytes > 0 ? Math.round(((storage.totalBytes - storage.availableBytes) / storage.totalBytes) * 100) : 0

  const editor = (cfg: Cfg) => {
    const value = editing[cfg.key] ?? cfg.value
    if (cfg.key === 'notify.enabled') {
      return (
        <Select value={value} onChange={(v) => setEditing((e) => ({ ...e, [cfg.key]: String(v) }))}>
          <Select.Option value="true">启用</Select.Option>
          <Select.Option value="false">关闭</Select.Option>
        </Select>
      )
    }
    if (cfg.key === 'upload.max_file_size' || cfg.key === 'upload.chunk_size') {
      return (
        <InputNumber
          min={cfg.key === 'upload.chunk_size' ? 256 * 1024 : 1024 * 1024}
          max={cfg.key === 'upload.chunk_size' ? 64 * 1024 * 1024 : 20 * 1024 * 1024 * 1024}
          precision={0}
          value={Number(value)}
          onChange={(v) => setEditing((e) => ({ ...e, [cfg.key]: String(v) }))}
        />
      )
    }
    if (cfg.key === 'storage.warn_percent') {
      return (
        <InputNumber
          min={1}
          max={99}
          precision={0}
          suffix="%"
          value={Number(value)}
          onChange={(v) => setEditing((e) => ({ ...e, [cfg.key]: String(v) }))}
        />
      )
    }
    return <Input value={value} onChange={(v) => setEditing((e) => ({ ...e, [cfg.key]: v }))} />
  }

  return (
    <div>
      <Card className="page-card" title="存储状态" style={{ marginBottom: 16 }}>
        {storage && (
          <Space size={40} align="start">
            <Descriptions
              column={1}
              data={[
                { label: '存储根目录', value: storage.root },
                { label: '挂载点', value: storage.mountPoint || '-' },
                {
                  label: '容量',
                  value: `${fmtSize(storage.totalBytes - storage.availableBytes)} / ${fmtSize(storage.totalBytes)} 已用`,
                },
                {
                  label: '告警阈值',
                  value: storage.warning
                    ? <Tag color="red">已超过 {storage.warnPercent}% 阈值</Tag>
                    : <Tag color="green">正常（阈值 {storage.warnPercent}%）</Tag>,
                },
              ]}
            />
            <Progress type="circle" percent={usedPct} color={storage.warning ? '#f53f3f' : '#165dff'} />
          </Space>
        )}
      </Card>
      <Card
        className="page-card"
        title="系统参数"
        extra={
          <Button type="primary" disabled={dirty.length === 0} loading={saving} onClick={save}>
            保存修改（{dirty.length}）
          </Button>
        }
      >
        <Table
          rowKey="key"
          data={configs}
          scroll={{ x: 940 }}
          pagination={false}
          columns={[
            { title: '参数键', dataIndex: 'key', width: 220, align: 'center' as const, ellipsis: true, render: (v: string) => <Tag className="table-cell-tag-ellipsis" title={v}>{v}</Tag> },
            {
              title: '值',
              dataIndex: 'value',
              width: 300,
              render: (_v: string, r: Cfg) => editor(r),
            },
            { title: '说明', dataIndex: 'description', width: 240, ellipsis: true },
            { title: '更新时间', dataIndex: 'updatedAt', width: 180, align: 'center' as const, render: fmtTime },
          ]}
        />
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          文件上限/分片大小单位为字节；扩展名白名单不可为空；所有修改经后端校验并在同一事务提交。
        </Typography.Text>
      </Card>
    </div>
  )
}
