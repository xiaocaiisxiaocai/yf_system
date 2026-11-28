import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, Card, Empty, Input, Radio, Space, Spin } from '@arco-design/web-react'
import { IconDownload, IconSearch } from '@arco-design/web-react/icon'
import type { ApiResponses } from '../../api/types'
import LoadError from '../../components/LoadError'
import { buildMergedContactGrid, getDistinctGroups } from '../../outsource/contactsMerge'
import { saveBlob } from '../../utils/saveBlob'
import { useOem } from '../OemContext'
import '../../outsource/pages/OutsourceContactsPage.css'

type PortalMatrix = ApiResponses['GET /oem/contacts']
type PortalColumn = PortalMatrix['columns'][number]
type PortalRow = PortalMatrix['rows'][number]
const emptyMatrix: PortalMatrix = { columns: [], rows: [] }

export default function PortalContactsPage() {
  const { api } = useOem()
  const [data, setData] = useState<PortalMatrix | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [groupFilter, setGroupFilter] = useState('ALL')
  const [keyword, setKeyword] = useState('')
  const [exporting, setExporting] = useState(false)
  const seqRef = useRef(0)

  const load = useCallback(async () => {
    const seq = ++seqRef.current
    setLoading(true)
    setError(false)
    try {
      const response = await api.contactMatrix()
      if (seq === seqRef.current) setData(response)
    } catch {
      if (seq === seqRef.current) setError(true)
    } finally {
      if (seq === seqRef.current) setLoading(false)
    }
  }, [api])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void load() })
    return () => { active = false; seqRef.current += 1 }
  }, [load])

  const { columns, rows } = data ?? emptyMatrix
  const groups = useMemo(() => getDistinctGroups(rows, columns), [rows, columns])
  const activeGroup = groupFilter !== 'ALL' && groups.includes(groupFilter) ? groupFilter : 'ALL'
  const grid = useMemo(
    () => buildMergedContactGrid<PortalRow, PortalColumn>(columns, rows, { groupFilter: activeGroup, keyword }),
    [columns, rows, activeGroup, keyword],
  )

  const exportExcel = async () => {
    if (exporting || columns.length === 0) return
    setExporting(true)
    try {
      const day = new Date().toLocaleDateString('sv-SE', { timeZone: 'Asia/Shanghai' }).replaceAll('-', '')
      saveBlob(await api.contactMatrixExport(), `对接联络表_${day}.xlsx`)
    } catch {
      // 统一请求层提示
    } finally {
      setExporting(false)
    }
  }

  const renderCell = (col: PortalColumn, kind: string, row: PortalRow) => {
    if (kind === 'TEXT') {
      const value = row.values[col.colKey] ?? ''
      return value ? <span>{value}</span> : <span style={{ color: 'var(--color-text-3)' }}>—</span>
    }
    if (kind === 'MULTILINE') {
      const value = row.values[col.colKey] ?? ''
      return value ? <div className="contacts-multiline">{value}</div> : <span style={{ color: 'var(--color-text-3)' }}>—</span>
    }
    const people = row.people[col.colKey] ?? []
    if (people.length === 0) return <span style={{ color: 'var(--color-text-3)' }}>—</span>

    if (kind === 'PERSON_NAME') {
      return (
        <div>
          {people.map((p) => (p.prefixLabel ? `${p.prefixLabel}：${p.name}` : p.name)).join(' / ')}
        </div>
      )
    }
    if (kind === 'PERSON_PHONE') {
      const showName = people.length > 1
      return (
        <div className="contacts-person-list contacts-person-list--center">
          {people.map((p) => (
            <div key={p.personId} className="contacts-phone-plain">
              {showName ? `${p.name}：` : ''}{p.phone}
            </div>
          ))}
        </div>
      )
    }
    return (
      <div className="contacts-person-list">
        {people.map((p) => (
          <div key={p.personId} className="contacts-person-item">
            <span>{p.prefixLabel ? `${p.prefixLabel}：${p.name}` : p.name}</span>
            <span className="contacts-phone-plain">{p.phone}</span>
          </div>
        ))}
      </div>
    )
  }

  return (
    <Card className="page-card page-card--table contacts-page">
      <h1 className="visually-hidden">对接联络表</h1>
      <div className="contacts-toolbar">
        <Space wrap>
          {groups.length > 0 && (
            <Radio.Group type="button" value={activeGroup} onChange={(val) => setGroupFilter(String(val))}>
              <Radio value="ALL">全部 ({rows.length})</Radio>
              {groups.map((g) => (
                <Radio key={g} value={g}>{g}</Radio>
              ))}
            </Radio.Group>
          )}
          <Input
            allowClear
            prefix={<IconSearch />}
            placeholder="搜索姓名、电话或岗位"
            style={{ width: 240 }}
            value={keyword}
            onChange={(val) => setKeyword(val)}
          />
        </Space>
        <Button icon={<IconDownload />} loading={exporting} disabled={columns.length === 0 || rows.length === 0} onClick={() => void exportExcel()}>
          导出 Excel
        </Button>
      </div>

      {loading ? (
        <div style={{ padding: 48, textAlign: 'center' }}><Spin /></div>
      ) : error ? (
        <LoadError message="对接联络表加载失败" onRetry={() => void load()} />
      ) : columns.length === 0 || grid.rows.length === 0 ? (
        <Empty description="暂无可用的对接联络记录" />
      ) : (
        <div className="contacts-table-wrap">
          <table className="contacts-matrix-table">
            <thead>
              <tr>
                {grid.visualCols.map((vc) => <th key={vc.visualId}>{vc.title}</th>)}
              </tr>
            </thead>
            <tbody>
              {grid.grid.map((rowCells, rIdx) => {
                const rowObj = grid.rows[rIdx]
                const prevObj = rIdx > 0 ? grid.rows[rIdx - 1] : null
                const isNewGroup = Boolean(
                  grid.groupColumn
                  && prevObj
                  && (rowObj.values[grid.groupColumn.colKey] ?? '').trim() !== (prevObj.values[grid.groupColumn.colKey] ?? '').trim(),
                )
                return (
                  <tr key={rowObj.id} className={isNewGroup ? 'contacts-group-divider' : undefined}>
                    {rowCells.map((cell) => {
                      if (cell.skip) return null
                      const { visualCol, rowSpan } = cell
                      const isGroup = visualCol.isGroupColumn
                      const centered = isGroup || visualCol.kind === 'PERSON_NAME' || visualCol.kind === 'PERSON_PHONE' || visualCol.kind === 'TEXT'
                      return (
                        <td
                          key={visualCol.visualId}
                          rowSpan={rowSpan}
                          className={[isGroup ? 'contacts-cell--group' : '', centered ? 'contacts-cell--center' : ''].filter(Boolean).join(' ')}
                        >
                          {renderCell(visualCol.col, visualCol.kind, rowObj)}
                        </td>
                      )
                    })}
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}
