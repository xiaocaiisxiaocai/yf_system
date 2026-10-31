import { Button, Card, Input, InputNumber, Space, Table } from '@arco-design/web-react'
import type { Cfg } from './systemConfigModel'
import { CONFIG_META } from './systemConfigModel'

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
    const numeric = CONFIG_META[cfg.key]?.numeric
    if (numeric) {
      const scale = numeric.scale ?? 1
      const numericValue = Number(value)
      return (
        <InputNumber
          aria-label={label}
          min={numeric.min}
          max={numeric.max}
          precision={numeric.precision ?? 0}
          step={numeric.step ?? 1}
          suffix={numeric.unit}
          value={value !== '' && Number.isFinite(numericValue) ? numericValue / scale : undefined}
          disabled={saving}
          onChange={(next) => {
            if (typeof next === 'number' && Number.isFinite(next)) {
              onSetValue(cfg.key, String(Math.round(next * scale)))
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
      title="上传限制与数据保留"
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
