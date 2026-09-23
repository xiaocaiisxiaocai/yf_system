import { Button, Card, Input, InputNumber, Select, Space, Table } from '@arco-design/web-react'
import type { Cfg } from './systemConfigModel'
import { CONFIG_META, MB } from './systemConfigModel'

interface SystemConfigTableProps {
  configs: Cfg[]
  editing: Record<string, string>
  dirty: Array<[string, string]>
  dirtyKeys: Set<string>
  saving: boolean
  formatTime: (value?: string | null) => string
  onReset: () => void
  onSave: () => void
  onSetValue: (key: string, value: string) => void
}

export function renderSystemConfigTable({
  configs,
  editing,
  dirty,
  dirtyKeys,
  saving,
  formatTime,
  onReset,
  onSave,
  onSetValue,
}: SystemConfigTableProps) {
  const editor = (cfg: Cfg) => {
    const value = editing[cfg.key] ?? cfg.value
    const label = CONFIG_META[cfg.key]?.name || cfg.key
    if (cfg.key === 'notify.enabled') {
      return (
        <Select aria-label={label} value={value} disabled={saving} onChange={(next) => onSetValue(cfg.key, String(next))}>
          <Select.Option value="true">启用</Select.Option>
          <Select.Option value="false">关闭</Select.Option>
        </Select>
      )
    }
    if (cfg.key === 'upload.max_file_size' || cfg.key === 'upload.chunk_size') {
      const isChunkSize = cfg.key === 'upload.chunk_size'
      const numericValue = Number(value)
      return (
        <InputNumber
          aria-label={label}
          min={isChunkSize ? 0.25 : 1}
          max={isChunkSize ? 64 : 20 * 1024}
          precision={isChunkSize ? 2 : 0}
          step={isChunkSize ? 0.25 : 1}
          suffix="MB"
          value={Number.isFinite(numericValue) ? numericValue / MB : undefined}
          disabled={saving}
          onChange={(next) => {
            if (typeof next === 'number' && Number.isFinite(next)) {
              onSetValue(cfg.key, String(Math.round(next * MB)))
            } else {
              onSetValue(cfg.key, '')
            }
          }}
        />
      )
    }
    return <Input aria-label={label} value={value} disabled={saving} onChange={(next) => onSetValue(cfg.key, next)} />
  }

  return (
    <Card
      className="page-card system-config-card"
      title="系统参数"
      extra={
        <Space size={8}>
          {dirty.length > 0 && <span className="system-config-dirty">已修改 {dirty.length} 项</span>}
          <Button disabled={dirty.length === 0 || saving} onClick={onReset}>重置</Button>
          <Button type="primary" disabled={dirty.length === 0 || saving} loading={saving} onClick={onSave}>保存</Button>
        </Space>
      }
    >
      <Table
        rowKey="key"
        data={configs}
        scroll={{ x: 860 }}
        pagination={false}
        rowClassName={(record) => dirtyKeys.has(record.key) ? 'system-config-row--dirty' : ''}
        columns={[
          {
            title: '参数',
            dataIndex: 'key',
            width: 250,
            render: (key: string) => (
              <div className="system-config-param">
                <strong>{CONFIG_META[key]?.name || key}</strong>
                <code title={key}>{key}</code>
              </div>
            ),
          },
          {
            title: '设置',
            dataIndex: 'value',
            width: 340,
            render: (_value: string, record: Cfg) => (
              <div className="system-config-editor">
                {editor(record)}
                {CONFIG_META[record.key]?.hint && <span>{CONFIG_META[record.key].hint}</span>}
              </div>
            ),
          },
          {
            title: '说明',
            dataIndex: 'description',
            width: 270,
            render: (description: string, record: Cfg) => (
              <div className="system-config-description">
                <span>{CONFIG_META[record.key]?.description || description || '-'}</span>
                {record.updatedAt && <small>更新于 {formatTime(record.updatedAt)}</small>}
              </div>
            ),
          },
        ]}
      />
    </Card>
  )
}
