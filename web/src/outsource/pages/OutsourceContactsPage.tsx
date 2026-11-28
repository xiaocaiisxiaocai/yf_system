import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert, Button, Card, Checkbox, Drawer, Empty, Input, InputNumber, Message, Modal, Radio, Select, Space, Spin, Tabs, Tag,
} from '@arco-design/web-react'
import type { TableColumnProps } from '@arco-design/web-react'
import {
  IconArrowDown, IconArrowUp, IconCopy, IconDelete, IconDownload, IconEdit, IconPhone, IconPlus, IconSearch, IconSettings, IconSwap, IconUpload,
} from '@arco-design/web-react/icon'
import http from '../../api/client'
import { requestErrorMessage } from '../../api/requestError'
import type { ApiResponses } from '../../api/types'
import DangerPopconfirm from '../../components/DangerPopconfirm'
import LoadError from '../../components/LoadError'
import ResizableTable from '../../components/ResizableTable'
import { saveBlob } from '../../utils/saveBlob'
import { buildMergedContactGrid, getDistinctGroups } from '../contactsMerge'
import { parseContactsWorkbook, type ContactImportRowPayload } from '../contactsWorkbook'
import './OutsourceContactsPage.css'

type MatrixResponse = ApiResponses['GET /outsource/contacts']
type ContactColumn = MatrixResponse['columns'][number]
type ContactRow = MatrixResponse['rows'][number]
type PeopleResponse = ApiResponses['GET /outsource/contacts/people']
type ContactPerson = PeopleResponse['people'][number]
type PersonRef = ApiResponses['GET /outsource/contacts/people/{id}/references'][number]
type UserOption = ApiResponses['GET /outsource/contacts/user-options'][number]
type ImportPreview = ApiResponses['POST /outsource/contacts/import/preview']

type TabKey = 'matrix' | 'people'

const VISIBILITY_LABELS: Record<string, string> = {
  INTERNAL_ONLY: '仅内部可见',
  ALL_VENDORS: '全部外包',
  SPECIFIC_VENDORS: '仅指定外包',
}

const DATA_TYPE_LABELS: Record<string, string> = {
  TEXT: '单行文本',
  MULTILINE: '多行文本',
  PERSON: '人员与电话',
}

export default function OutsourceContactsPage() {
  const [tab, setTab] = useState<TabKey>('matrix')
  const [matrix, setMatrix] = useState<MatrixResponse | null>(null)
  const [peopleData, setPeopleData] = useState<PeopleResponse | null>(null)
  const [userOptions, setUserOptions] = useState<UserOption[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [saving, setSaving] = useState(false)
  const [exporting, setExporting] = useState(false)

  // Filters & selection
  const [groupFilter, setGroupFilter] = useState('ALL')
  const [keyword, setKeyword] = useState('')
  const [vendorPreviewId, setVendorPreviewId] = useState<number | undefined>(undefined)
  const [selectedRowIds, setSelectedRowIds] = useState<number[]>([])
  const [pulsedPersonId, setPulsedPersonId] = useState<number | null>(null)

  // People tab filters
  const [peopleScopeFilter, setPeopleScopeFilter] = useState<'ALL' | 'INTERNAL' | 'VENDOR'>('ALL')
  const [peopleSearch, setPeopleSearch] = useState('')

  // Modals / drawers
  const [columnsDrawerOpen, setColumnsDrawerOpen] = useState(false)
  const [colEditor, setColEditor] = useState<{
    mode: 'CREATE' | 'EDIT'
    id?: number
    title: string
    dataType: string
    personDisplay: string
    phoneTitle: string
    autoMerge: boolean
    vendorVisible: boolean
    isGroupColumn: boolean
    widthHint: number
  } | null>(null)

  const [cellModal, setCellModal] = useState<{
    column: ContactColumn
    rowIds: number[]
    applyToAll: boolean
    textValue: string
    peopleList: Array<{ personId: number; prefixLabel: string }>
    pickedPersonId?: number
  } | null>(null)

  const [visModal, setVisModal] = useState<{
    rowIds: number[]
    visibility: string
    vendorIds: number[]
  } | null>(null)

  const [phoneModal, setPhoneModal] = useState<{
    person: ContactPerson
    phone: string
    refs: PersonRef[]
    loadingRefs: boolean
  } | null>(null)

  const [personModal, setPersonModal] = useState<{
    mode: 'CREATE' | 'EDIT'
    id?: number
    scopeType: 'INTERNAL' | 'VENDOR'
    vendorId?: number
    userId?: number
    name: string
    phone: string
    email: string
    titleHint: string
    returnToCellModal?: boolean
  } | null>(null)

  const [handoverModal, setHandoverModal] = useState<{
    source: ContactPerson
    refs: PersonRef[]
    selectedRowIds: number[]
    targetPersonId?: number
  } | null>(null)

  const [refsModal, setRefsModal] = useState<{
    person: ContactPerson
    refs: PersonRef[]
  } | null>(null)

  const [importModal, setImportModal] = useState<{
    mode: 'APPEND' | 'REPLACE_ALL' | 'REPLACE_GROUPS'
    parsedRows: ContactImportRowPayload[]
    preview: ImportPreview | null
    overrides: Record<string, number | null>
    confirmPhoneUpdates: boolean
    parsing: boolean
    fileName: string
  } | null>(null)

  const seqRef = useRef(0)
  const importFileRef = useRef<HTMLInputElement | null>(null)
  const importPreviewSeq = useRef(0)

  const loadAll = useCallback(async () => {
    const seq = ++seqRef.current
    setLoading(true)
    setError(false)
    try {
      const [matRes, pplRes] = await Promise.all([
        http.get<ApiResponses['GET /outsource/contacts']>('/outsource/contacts'),
        http.get<ApiResponses['GET /outsource/contacts/people']>('/outsource/contacts/people'),
      ])
      if (seq === seqRef.current) {
        setMatrix(matRes.data)
        setPeopleData(pplRes.data)
      }
    } catch {
      if (seq === seqRef.current) setError(true)
    } finally {
      if (seq === seqRef.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    let active = true
    void Promise.resolve().then(() => { if (active) void loadAll() })
    return () => { active = false; seqRef.current += 1 }
  }, [loadAll])

  const columns = useMemo(() => matrix?.columns ?? [], [matrix?.columns])
  const allRows = useMemo(() => matrix?.rows ?? [], [matrix?.rows])
  const vendors = useMemo(() => matrix?.vendors ?? [], [matrix?.vendors])
  const allPeople = useMemo(() => peopleData?.people ?? [], [peopleData?.people])
  const canManage = matrix?.canManage ?? false
  const canConfigureColumns = matrix?.canConfigureColumns ?? false

  const vendorNamesById = useMemo(() => new Map(vendors.map((v) => [v.id, v.name])), [vendors])
  const groups = useMemo(() => getDistinctGroups(allRows, columns), [allRows, columns])
  const activeGroup = groupFilter !== 'ALL' && groups.includes(groupFilter) ? groupFilter : 'ALL'

  const previewFilteredRows = useMemo(() => {
    if (!vendorPreviewId) return allRows
    return allRows.filter((r) => r.visibility === 'ALL_VENDORS'
      || (r.visibility === 'SPECIFIC_VENDORS' && r.vendorIds.includes(vendorPreviewId)))
  }, [allRows, vendorPreviewId])

  const grid = useMemo(
    () => buildMergedContactGrid<ContactRow, ContactColumn>(columns, previewFilteredRows, {
      groupFilter: activeGroup,
      keyword,
      vendorNamesById,
    }),
    [columns, previewFilteredRows, activeGroup, keyword, vendorNamesById],
  )

  const rowById = useMemo(() => new Map(allRows.map((r) => [r.id, r])), [allRows])
  const personById = useMemo(() => new Map(allPeople.map((p) => [p.id, p])), [allPeople])

  const ensureUserOptionsLoaded = async () => {
    if (!canManage || userOptions.length > 0) return
    try {
      const res = await http.get<ApiResponses['GET /outsource/contacts/user-options']>('/outsource/contacts/user-options')
      setUserOptions(res.data)
    } catch {
      // ignore optional options failure
    }
  }

  const pulse = (personId: number) => {
    setPulsedPersonId(personId)
    window.setTimeout(() => setPulsedPersonId((curr) => (curr === personId ? null : curr)), 2200)
  }

  const runAction = async (fn: () => Promise<void>) => {
    if (saving) return
    setSaving(true)
    try {
      await fn()
    } catch (err) {
      Message.error(requestErrorMessage(err, '操作失败'))
    } finally {
      setSaving(false)
    }
  }

  // ---- Export & Template ----
  const handleExport = async () => {
    if (exporting || columns.length === 0) return
    setExporting(true)
    try {
      const res = await http.get<ApiResponses['GET /outsource/contacts/export']>('/outsource/contacts/export', {
        params: {
          group: activeGroup !== 'ALL' ? activeGroup : undefined,
          keyword: keyword.trim() || undefined,
          vendorId: vendorPreviewId,
        },
        responseType: 'blob',
        timeout: 0,
      })
      const day = new Date().toLocaleDateString('sv-SE', { timeZone: 'Asia/Shanghai' }).replaceAll('-', '')
      saveBlob(res.data, `对接联络表_${day}.xlsx`)
    } catch (err) {
      Message.error(requestErrorMessage(err, '导出失败'))
    } finally {
      setExporting(false)
    }
  }

  const handleDownloadTemplate = async () => {
    try {
      const res = await http.get<ApiResponses['GET /outsource/contacts/template']>('/outsource/contacts/template', {
        responseType: 'blob',
        timeout: 0,
      })
      saveBlob(res.data, '对接联络表导入模板.xlsx')
    } catch (err) {
      Message.error(requestErrorMessage(err, '下载模板失败'))
    }
  }

  // ---- Row operations ----
  const handleAddRow = () => runAction(async () => {
    await http.post<ApiResponses['POST /outsource/contacts/rows']>('/outsource/contacts/rows', {})
    Message.success('已新增一行（默认仅内部可见）')
    await loadAll()
  })

  const handleDuplicateRow = (rowId: number) => runAction(async () => {
    await http.post<ApiResponses['POST /outsource/contacts/rows/{id}/duplicate']>(`/outsource/contacts/rows/${rowId}/duplicate`, {})
    Message.success('已在下方复制一行')
    await loadAll()
  })

  const handleMoveRow = (rowId: number, dir: -1 | 1) => runAction(async () => {
    const idx = allRows.findIndex((r) => r.id === rowId)
    const target = idx + dir
    if (idx < 0 || target < 0 || target >= allRows.length) return
    const nextIds = allRows.map((r) => r.id)
    const tmp = nextIds[idx]
    nextIds[idx] = nextIds[target]
    nextIds[target] = tmp
    await http.put<ApiResponses['PUT /outsource/contacts/rows/order']>('/outsource/contacts/rows/order', { rowIds: nextIds })
    await loadAll()
  })

  const handleDeleteRow = (row: ContactRow) => runAction(async () => {
    await http.delete<ApiResponses['DELETE /outsource/contacts/rows/{id}']>(`/outsource/contacts/rows/${row.id}`, {
      params: { expectedVersion: row.version },
    })
    setSelectedRowIds((prev) => prev.filter((id) => id !== row.id))
    Message.success('已删除该行')
    await loadAll()
  })

  // ---- Cell edit ----
  const openCellEdit = (column: ContactColumn, rowIds: number[]) => {
    if (!canManage || rowIds.length === 0) return
    const first = rowById.get(rowIds[0])
    if (!first) return
    setCellModal({
      column,
      rowIds,
      applyToAll: true,
      textValue: first.values[column.colKey] ?? '',
      peopleList: (first.people[column.colKey] ?? []).map((p) => ({
        personId: p.personId,
        prefixLabel: p.prefixLabel ?? '',
      })),
    })
  }

  const handleSaveCell = () => {
    if (!cellModal) return
    return runAction(async () => {
      const targetIds = cellModal.applyToAll ? cellModal.rowIds : [cellModal.rowIds[0]]
      const rowsPayload = targetIds.map((id) => {
        const r = rowById.get(id)
        return { rowId: id, expectedVersion: r?.version ?? 1 }
      })
      await http.put<ApiResponses['PUT /outsource/contacts/cells']>('/outsource/contacts/cells', {
        columnId: cellModal.column.id,
        rows: rowsPayload,
        textValue: cellModal.column.dataType === 'PERSON' ? null : cellModal.textValue,
        people: cellModal.column.dataType === 'PERSON'
          ? cellModal.peopleList.map((p) => ({ personId: p.personId, prefixLabel: p.prefixLabel.trim() || null }))
          : null,
      })
      setCellModal(null)
      Message.success('已保存修改')
      await loadAll()
    })
  }

  // ---- Visibility ----
  const openVisibilityModal = (rowIds: number[]) => {
    if (!canManage || rowIds.length === 0) return
    const first = rowById.get(rowIds[0])
    setVisModal({
      rowIds,
      visibility: first?.visibility ?? 'INTERNAL_ONLY',
      vendorIds: first?.vendorIds ? [...first.vendorIds] : [],
    })
  }

  const handleSaveVisibility = () => {
    if (!visModal) return
    return runAction(async () => {
      const rowsPayload = visModal.rowIds.map((id) => ({
        rowId: id,
        expectedVersion: rowById.get(id)?.version ?? 1,
      }))
      await http.put<ApiResponses['PUT /outsource/contacts/rows/visibility']>('/outsource/contacts/rows/visibility', {
        rows: rowsPayload,
        visibility: visModal.visibility,
        vendorIds: visModal.visibility === 'SPECIFIC_VENDORS' ? visModal.vendorIds : [],
      })
      setVisModal(null)
      setSelectedRowIds([])
      Message.success('已更新可见范围')
      await loadAll()
    })
  }

  // ---- Quick phone linkage ----
  const openQuickPhone = async (personId: number) => {
    if (!canManage) return
    const person = personById.get(personId)
    if (!person) return
    setPhoneModal({ person, phone: person.phone, refs: [], loadingRefs: true })
    try {
      const res = await http.get<ApiResponses['GET /outsource/contacts/people/{id}/references']>(
        `/outsource/contacts/people/${personId}/references`,
      )
      setPhoneModal((curr) => (curr && curr.person.id === personId ? { ...curr, refs: res.data, loadingRefs: false } : curr))
    } catch {
      setPhoneModal((curr) => (curr && curr.person.id === personId ? { ...curr, loadingRefs: false } : curr))
    }
  }

  const handleSaveQuickPhone = () => {
    if (!phoneModal) return
    const { person, phone } = phoneModal
    return runAction(async () => {
      await http.put<ApiResponses['PUT /outsource/contacts/people/{id}']>(`/outsource/contacts/people/${person.id}`, {
        scopeType: person.scopeType,
        vendorId: person.vendorId,
        userId: person.userId,
        name: person.name,
        phone: phone.trim(),
        email: person.email,
        titleHint: person.titleHint,
      })
      setPhoneModal(null)
      Message.success(`已更新「${person.name}」电话并全局联动同步`)
      await loadAll()
      pulse(person.id)
    })
  }

  // ---- Person master CRUD ----
  const openCreatePerson = async (returnToCellModal = false) => {
    await ensureUserOptionsLoaded()
    setPersonModal({
      mode: 'CREATE',
      scopeType: 'INTERNAL',
      name: '',
      phone: '',
      email: '',
      titleHint: '',
      returnToCellModal,
    })
  }

  const openEditPerson = async (p: ContactPerson) => {
    await ensureUserOptionsLoaded()
    setPersonModal({
      mode: 'EDIT',
      id: p.id,
      scopeType: p.scopeType === 'VENDOR' ? 'VENDOR' : 'INTERNAL',
      vendorId: p.vendorId ?? undefined,
      userId: p.userId ?? undefined,
      name: p.name,
      phone: p.phone,
      email: p.email ?? '',
      titleHint: p.titleHint ?? '',
    })
  }

  const handleSavePerson = () => {
    if (!personModal) return
    return runAction(async () => {
      const payload = {
        scopeType: personModal.scopeType,
        vendorId: personModal.scopeType === 'VENDOR' ? personModal.vendorId : null,
        userId: personModal.scopeType === 'INTERNAL' ? (personModal.userId ?? null) : null,
        name: personModal.name.trim(),
        phone: personModal.phone.trim(),
        email: personModal.email.trim() || null,
        titleHint: personModal.titleHint.trim() || null,
      }
      if (personModal.mode === 'CREATE') {
        const res = await http.post<ApiResponses['POST /outsource/contacts/people']>('/outsource/contacts/people', payload)
        Message.success(`已新建「${res.data.name}」`)
        if (personModal.returnToCellModal) {
          setCellModal((curr) => (curr ? {
            ...curr,
            peopleList: [...curr.peopleList, { personId: res.data.id, prefixLabel: '' }],
          } : curr))
        }
      } else if (personModal.id) {
        const res = await http.put<ApiResponses['PUT /outsource/contacts/people/{id}']>(
          `/outsource/contacts/people/${personModal.id}`,
          payload,
        )
        Message.success(`已更新「${res.data.name}」`)
        pulse(res.data.id)
      }
      setPersonModal(null)
      await loadAll()
    })
  }

  const handleTogglePersonStatus = (p: ContactPerson) => runAction(async () => {
    const nextStatus = p.status === 'ACTIVE' ? 'DISABLED' : 'ACTIVE'
    await http.put<ApiResponses['PUT /outsource/contacts/people/{id}/status']>(
      `/outsource/contacts/people/${p.id}/status`,
      { status: nextStatus },
    )
    Message.success(nextStatus === 'DISABLED' ? `已停用「${p.name}」（外包门户自动隐藏）` : `已恢复启用「${p.name}」`)
    await loadAll()
  })

  const openPersonRefs = async (p: ContactPerson) => {
    try {
      const res = await http.get<ApiResponses['GET /outsource/contacts/people/{id}/references']>(
        `/outsource/contacts/people/${p.id}/references`,
      )
      setRefsModal({ person: p, refs: res.data })
    } catch (err) {
      Message.error(requestErrorMessage(err, '查询引用失败'))
    }
  }

  const openHandover = async (p: ContactPerson) => {
    try {
      const res = await http.get<ApiResponses['GET /outsource/contacts/people/{id}/references']>(
        `/outsource/contacts/people/${p.id}/references`,
      )
      if (res.data.length === 0) {
        Message.info('该人员当前未被表格引用，可直接停用')
        return
      }
      const candidates = allPeople.filter((c) =>
        c.id !== p.id && c.status === 'ACTIVE' && c.scopeType === p.scopeType && c.vendorId === p.vendorId)
      setHandoverModal({
        source: p,
        refs: res.data,
        selectedRowIds: Array.from(new Set(res.data.map((r) => r.rowId))),
        targetPersonId: candidates[0]?.id,
      })
    } catch (err) {
      Message.error(requestErrorMessage(err, '加载交接信息失败'))
    }
  }

  const handleSaveHandover = () => {
    if (!handoverModal || !handoverModal.targetPersonId) {
      Message.warning('请选择接替人员')
      return
    }
    return runAction(async () => {
      const res = await http.post<ApiResponses['POST /outsource/contacts/people/{id}/handover']>(
        `/outsource/contacts/people/${handoverModal.source.id}/handover`,
        {
          targetPersonId: handoverModal.targetPersonId,
          rowIds: handoverModal.selectedRowIds,
        },
      )
      const targetId = handoverModal.targetPersonId!
      setHandoverModal(null)
      Message.success(`已完成交接，共替换 ${res.data.replacedCount} 处`)
      await loadAll()
      pulse(targetId)
    })
  }

  // ---- Column configuration (outsource:contact_settings) ----
  const handleMoveColumn = (colId: number, dir: -1 | 1) => runAction(async () => {
    const idx = columns.findIndex((c) => c.id === colId)
    const target = idx + dir
    if (idx < 0 || target < 0 || target >= columns.length) return
    const nextIds = columns.map((c) => c.id)
    const tmp = nextIds[idx]
    nextIds[idx] = nextIds[target]
    nextIds[target] = tmp
    await http.put<ApiResponses['PUT /outsource/contacts/columns/order']>('/outsource/contacts/columns/order', { columnIds: nextIds })
    await loadAll()
  })

  const handleDeleteColumn = (col: ContactColumn) => runAction(async () => {
    await http.delete<ApiResponses['DELETE /outsource/contacts/columns/{id}']>(`/outsource/contacts/columns/${col.id}`)
    Message.success(`已删除列「${col.title}」`)
    await loadAll()
  })

  const handleSaveColumn = () => {
    if (!colEditor) return
    return runAction(async () => {
      if (colEditor.mode === 'CREATE') {
        await http.post<ApiResponses['POST /outsource/contacts/columns']>('/outsource/contacts/columns', {
          title: colEditor.title.trim(),
          dataType: colEditor.dataType,
          personDisplay: colEditor.dataType === 'PERSON' ? colEditor.personDisplay : 'COMBINED',
          phoneTitle: colEditor.dataType === 'PERSON' && colEditor.personDisplay === 'SPLIT' ? colEditor.phoneTitle.trim() || null : null,
          autoMerge: colEditor.autoMerge,
          vendorVisible: colEditor.vendorVisible,
          isGroupColumn: colEditor.isGroupColumn,
          widthHint: colEditor.widthHint,
        })
        Message.success('已新增列')
      } else if (colEditor.id) {
        await http.put<ApiResponses['PUT /outsource/contacts/columns/{id}']>(`/outsource/contacts/columns/${colEditor.id}`, {
          title: colEditor.title.trim(),
          dataType: colEditor.dataType,
          personDisplay: colEditor.dataType === 'PERSON' ? colEditor.personDisplay : 'COMBINED',
          phoneTitle: colEditor.dataType === 'PERSON' && colEditor.personDisplay === 'SPLIT' ? colEditor.phoneTitle.trim() || null : null,
          autoMerge: colEditor.autoMerge,
          vendorVisible: colEditor.vendorVisible,
          isGroupColumn: colEditor.isGroupColumn,
          widthHint: colEditor.widthHint,
        })
        Message.success('已更新列设置')
      }
      setColEditor(null)
      await loadAll()
    })
  }

  // ---- Excel Import ----
  const runImportPreview = async (
    mode: 'APPEND' | 'REPLACE_ALL' | 'REPLACE_GROUPS',
    parsedRows: ContactImportRowPayload[],
    overrides: Record<string, number | null>,
    fileName: string,
  ) => {
    const seq = ++importPreviewSeq.current
    setImportModal((curr) => curr && ({
      mode, parsedRows, overrides, fileName, preview: null, confirmPhoneUpdates: false, parsing: true,
    }))
    const personOverrides = Object.entries(overrides).map(([matchKey, vendorId]) => ({ matchKey, vendorId }))
    try {
      const res = await http.post<ApiResponses['POST /outsource/contacts/import/preview']>(
        '/outsource/contacts/import/preview',
        { mode, rows: parsedRows, personOverrides },
      )
      if (seq === importPreviewSeq.current)
        setImportModal((curr) => curr && { ...curr, preview: res.data, parsing: false })
    } catch (err) {
      if (seq !== importPreviewSeq.current) return
      setImportModal((curr) => curr && { ...curr, parsing: false })
      Message.error(requestErrorMessage(err, '导入预览失败'))
    }
  }

  const handlePickImportFile = async (file: File) => {
    if (!importModal) return
    const seq = ++importPreviewSeq.current
    setImportModal({ ...importModal, parsing: true, fileName: file.name, parsedRows: [],
      preview: null, overrides: {}, confirmPhoneUpdates: false })
    try {
      const parsed = await parseContactsWorkbook(file, columns)
      if (seq === importPreviewSeq.current) await runImportPreview(importModal.mode, parsed, {}, file.name)
    } catch (err) {
      if (seq !== importPreviewSeq.current) return
      setImportModal((curr) => (curr ? { ...curr, parsing: false } : curr))
      Message.error(err instanceof Error ? err.message : requestErrorMessage(err, '解析 Excel 失败'))
    }
  }

  const handleConfirmImport = () => {
    if (!importModal?.preview || importModal.parsing) return
    const draft = importModal
    const previewVersion = importModal.preview.previewVersion
    return runAction(async () => {
      const personOverrides = Object.entries(draft.overrides).map(([matchKey, vendorId]) => ({ matchKey, vendorId }))
      const res = await http.post<ApiResponses['POST /outsource/contacts/import']>('/outsource/contacts/import', {
        mode: draft.mode,
        rows: draft.parsedRows,
        personOverrides,
        confirmPhoneUpdates: draft.confirmPhoneUpdates,
        previewVersion,
      }).catch((err: unknown) => {
        if ((err as { response?: { status?: number } } | null)?.response?.status === 409) {
          setImportModal((curr) => curr && { ...curr, preview: null, confirmPhoneUpdates: false })
        }
        throw err
      })
      setImportModal(null)
      Message.success(`导入成功：写入 ${res.data.inserted} 行，新建人员 ${res.data.createdPeople} 位，联动更新电话 ${res.data.updatedPhones} 位`)
      await loadAll()
    })
  }

  // ---- Cell Renderers ----
  const renderCellBody = (col: ContactColumn, kind: string, row: ContactRow) => {
    if (kind === 'TEXT') {
      const val = row.values[col.colKey] ?? ''
      return val ? <span>{val}</span> : <span style={{ color: 'var(--color-text-3)' }}>—</span>
    }
    if (kind === 'MULTILINE') {
      const val = row.values[col.colKey] ?? ''
      return val ? <div className="contacts-multiline">{val}</div> : <span style={{ color: 'var(--color-text-3)' }}>—</span>
    }
    const people = row.people[col.colKey] ?? []
    if (people.length === 0) return <span style={{ color: 'var(--color-text-3)' }}>—</span>

    if (kind === 'PERSON_NAME') {
      return (
        <div>
          {people.map((p) => {
            const disabledTag = p.status !== 'ACTIVE' ? '（停用）' : ''
            return `${p.prefixLabel ? `${p.prefixLabel}：` : ''}${p.name}${disabledTag}`
          }).join(' / ')}
        </div>
      )
    }
    if (kind === 'PERSON_PHONE') {
      const showName = people.length > 1
      return (
        <div className="contacts-person-list contacts-person-list--center">
          {people.map((p) => (
            <div key={p.personId} className={pulsedPersonId === p.personId ? 'contacts-sync-pulse' : undefined}>
              {showName ? <span style={{ color: 'var(--color-text-3)' }}>{p.name}：</span> : null}
              {canManage ? (
                <button
                  type="button"
                  className="contacts-phone-link"
                  title={`点击联动修改「${p.name}」的电话`}
                  onClick={(e) => { e.stopPropagation(); void openQuickPhone(p.personId) }}
                >
                  {p.phone}
                </button>
              ) : (
                <span className="contacts-phone-plain">{p.phone}</span>
              )}
            </div>
          ))}
        </div>
      )
    }
    return (
      <div className="contacts-person-list">
        {people.map((p) => (
          <div
            key={p.personId}
            className={[
              'contacts-person-item',
              p.status !== 'ACTIVE' ? 'contacts-person-item--disabled' : '',
              pulsedPersonId === p.personId ? 'contacts-sync-pulse' : '',
            ].filter(Boolean).join(' ')}
          >
            <span>
              {p.prefixLabel ? <span style={{ color: 'var(--color-text-3)' }}>{p.prefixLabel}：</span> : null}
              {p.name}
              {p.status !== 'ACTIVE' && <Tag size="small" color="red" style={{ marginLeft: 4 }}>已停用</Tag>}
              {p.scopeType === 'VENDOR' && p.vendorName && (
                <Tag size="small" color="orangered" style={{ marginLeft: 4 }}>{p.vendorName}</Tag>
              )}
            </span>
            {canManage ? (
              <button
                type="button"
                className="contacts-phone-link"
                title={`点击联动修改「${p.name}」的电话`}
                onClick={(e) => { e.stopPropagation(); void openQuickPhone(p.personId) }}
              >
                {p.phone}
              </button>
            ) : (
              <span className="contacts-phone-plain">{p.phone}</span>
            )}
          </div>
        ))}
      </div>
    )
  }

  const renderVisibilityCell = (row: ContactRow) => {
    const badge = row.visibility === 'INTERNAL_ONLY'
      ? <Tag color="gray">{VISIBILITY_LABELS.INTERNAL_ONLY}</Tag>
      : row.visibility === 'ALL_VENDORS'
        ? <Tag color="green">{VISIBILITY_LABELS.ALL_VENDORS}</Tag>
        : row.vendorIds.length === 0
          ? <Tag color="gray">未选外包</Tag>
          : (
            <Space size={4} wrap>
              {row.vendorIds.map((id) => (
                <Tag key={id} color="arcoblue">{vendorNamesById.get(id) ?? `厂商#${id}`}</Tag>
              ))}
            </Space>
          )
    if (!canManage) return badge
    return (
      <button type="button" className="contacts-visibility-btn" onClick={() => openVisibilityModal([row.id])}>
        <span>{badge}</span>
        <IconEdit style={{ color: 'var(--color-text-3)', flexShrink: 0 }} />
      </button>
    )
  }

  // ---- Filtered people list ----
  const filteredPeople = useMemo(() => {
    const q = peopleSearch.trim().toLowerCase()
    return allPeople.filter((p) => {
      if (peopleScopeFilter !== 'ALL' && p.scopeType !== peopleScopeFilter) return false
      if (!q) return true
      return p.name.toLowerCase().includes(q)
        || p.phone.toLowerCase().includes(q)
        || (p.titleHint ?? '').toLowerCase().includes(q)
        || (p.vendorName ?? '').toLowerCase().includes(q)
    })
  }, [allPeople, peopleScopeFilter, peopleSearch])

  const peopleColumns: TableColumnProps<ContactPerson>[] = [
    {
      title: '姓名与备注',
      dataIndex: 'name',
      render: (_, p) => (
        <div>
          <div style={{ fontWeight: 500 }}>{p.name}</div>
          {p.titleHint && <div style={{ fontSize: 12, color: 'var(--color-text-3)' }}>{p.titleHint}</div>}
        </div>
      ),
    },
    {
      title: '人员归属',
      dataIndex: 'scopeType',
      width: 180,
      render: (_, p) => p.scopeType === 'VENDOR'
        ? <Tag color="orangered">外包：{p.vendorName ?? `厂商#${p.vendorId}`}</Tag>
        : <Tag color="arcoblue">我司内部{p.userEmployeeNo ? ` (${p.userEmployeeNo})` : ''}</Tag>,
    },
    {
      title: '联系电话（联动唯一源）',
      dataIndex: 'phone',
      width: 170,
      render: (_, p) => (
        <span className={`contacts-phone-plain ${pulsedPersonId === p.id ? 'contacts-sync-pulse' : ''}`}>
          {p.phone}
        </span>
      ),
    },
    {
      title: '状态',
      dataIndex: 'status',
      width: 90,
      render: (status) => status === 'ACTIVE' ? <Tag color="green">启用</Tag> : <Tag color="red">已停用</Tag>,
    },
    {
      title: '操作',
      width: canManage ? 280 : 110,
      render: (_, p) => (
        <Space size="mini" wrap>
          <Button size="mini" onClick={() => void openPersonRefs(p)}>引用位置</Button>
          {canManage && (
            <>
              <Button size="mini" icon={<IconPhone />} onClick={() => void openQuickPhone(p.id)}>改电话</Button>
              <Button size="mini" icon={<IconEdit />} onClick={() => void openEditPerson(p)}>编辑</Button>
              <Button size="mini" icon={<IconSwap />} onClick={() => void openHandover(p)}>交接</Button>
              <DangerPopconfirm
                title={p.status === 'ACTIVE' ? `确认停用「${p.name}」？停用后外包门户将自动隐藏该联系人。` : `确认恢复启用「${p.name}」？`}
                onOk={() => void handleTogglePersonStatus(p)}
              >
                <Button size="mini" status={p.status === 'ACTIVE' ? 'warning' : 'success'}>
                  {p.status === 'ACTIVE' ? '停用' : '启用'}
                </Button>
              </DangerPopconfirm>
            </>
          )}
        </Space>
      ),
    },
  ]

  const allVisibleRowIds = useMemo(() => grid.rows.map((r) => r.id), [grid.rows])
  const allVisibleSelected = allVisibleRowIds.length > 0 && allVisibleRowIds.every((id) => selectedRowIds.includes(id))

  return (
    <Card className="page-card page-card--table contacts-page">
      <h1 className="visually-hidden">对接联络表</h1>

      <Tabs activeTab={tab} onChange={(val) => setTab(val as TabKey)}>
        <Tabs.TabPane key="matrix" title="对接联络总表" />
        <Tabs.TabPane key="people" title={`人员电话库 (${allPeople.length})`} />
      </Tabs>

      {loading ? (
        <div style={{ padding: 48, textAlign: 'center' }}><Spin /></div>
      ) : error ? (
        <LoadError message="对接联络表加载失败" onRetry={() => void loadAll()} />
      ) : tab === 'matrix' ? (
        <>
          <div className="contacts-toolbar">
            <Space wrap>
              {groups.length > 0 && (
                <Radio.Group type="button" value={activeGroup} onChange={(val) => setGroupFilter(String(val))}>
                  <Radio value="ALL">全部 ({previewFilteredRows.length})</Radio>
                  {groups.map((g) => (
                    <Radio key={g} value={g}>{g}</Radio>
                  ))}
                </Radio.Group>
              )}
              <Input
                allowClear
                prefix={<IconSearch />}
                placeholder="搜姓名、电话、内容或外包厂商"
                style={{ width: 240 }}
                value={keyword}
                onChange={(val) => setKeyword(val)}
              />
              <Select
                allowClear
                placeholder="按外包厂商筛选预览"
                style={{ width: 190 }}
                value={vendorPreviewId}
                onChange={(val) => setVendorPreviewId(val as number | undefined)}
              >
                {vendors.map((v) => (
                  <Select.Option key={v.id} value={v.id}>{v.name}</Select.Option>
                ))}
              </Select>
            </Space>

            <Space wrap>
              {canManage && selectedRowIds.length > 0 && (
                <Button type="primary" onClick={() => openVisibilityModal(selectedRowIds)}>
                  批量设置给谁看 ({selectedRowIds.length})
                </Button>
              )}
              <Button icon={<IconDownload />} loading={exporting} disabled={columns.length === 0} onClick={() => void handleExport()}>
                导出 Excel
              </Button>
              {canManage && columns.length > 0 && (
                <>
                  <Button icon={<IconDownload />} onClick={() => void handleDownloadTemplate()}>下载模板</Button>
                  <Button
                    icon={<IconUpload />}
                    onClick={() => setImportModal({
                      mode: 'APPEND',
                      parsedRows: [],
                      preview: null,
                      overrides: {},
                      confirmPhoneUpdates: false,
                      parsing: false,
                      fileName: '',
                    })}
                  >
                    Excel 导入
                  </Button>
                  <Button type="primary" icon={<IconPlus />} loading={saving} onClick={() => void handleAddRow()}>
                    新增一行
                  </Button>
                </>
              )}
              {canConfigureColumns && (
                <Button type={columns.length === 0 ? 'primary' : 'secondary'} icon={<IconSettings />} onClick={() => setColumnsDrawerOpen(true)}>
                  管理列
                </Button>
              )}
            </Space>
          </div>

          {columns.length === 0 ? (
            <div style={{ padding: '48px 0', textAlign: 'center' }}>
              <Empty
                description={canConfigureColumns ? '当前尚未配置联络表列结构，请点击「管理列」添加列' : '管理员尚未配置联络表结构'}
              />
              {canConfigureColumns && (
                <div style={{ marginTop: 12 }}>
                  <Button type="primary" icon={<IconSettings />} onClick={() => setColumnsDrawerOpen(true)}>
                    立即配置列
                  </Button>
                </div>
              )}
            </div>
          ) : grid.rows.length === 0 ? (
            <Empty description="当前条件下暂无联络记录" style={{ padding: '40px 0' }} />
          ) : (
            <div className="contacts-table-wrap">
              <table className="contacts-matrix-table">
                <thead>
                  <tr>
                    {canManage && (
                      <th style={{ width: 42 }}>
                        <Checkbox
                          checked={allVisibleSelected}
                          onChange={(checked) => setSelectedRowIds(checked ? allVisibleRowIds : [])}
                        />
                      </th>
                    )}
                    {grid.visualCols.map((vc) => <th key={vc.visualId}>{vc.title}</th>)}
                    <th style={{ minWidth: 150 }}>给谁看（可见范围）</th>
                    {canManage && <th style={{ width: 155 }}>行操作</th>}
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
                    const checked = selectedRowIds.includes(rowObj.id)
                    const rawIndex = allRows.findIndex((r) => r.id === rowObj.id)
                    return (
                      <tr key={rowObj.id} className={isNewGroup ? 'contacts-group-divider' : undefined}>
                        {canManage && (
                          <td className="contacts-cell--center">
                            <Checkbox
                              checked={checked}
                              onChange={(val) => setSelectedRowIds((prev) => (val ? [...prev, rowObj.id] : prev.filter((id) => id !== rowObj.id)))}
                            />
                          </td>
                        )}
                        {rowCells.map((cell) => {
                          if (cell.skip) return null
                          const { visualCol, rowIds, rowSpan } = cell
                          const isGroup = visualCol.isGroupColumn
                          const centered = isGroup || visualCol.kind === 'PERSON_NAME' || visualCol.kind === 'PERSON_PHONE' || visualCol.kind === 'TEXT'
                          return (
                            <td
                              key={visualCol.visualId}
                              rowSpan={rowSpan}
                              className={[
                                isGroup ? 'contacts-cell--group' : '',
                                centered ? 'contacts-cell--center' : '',
                                canManage ? 'contacts-cell--editable' : '',
                              ].filter(Boolean).join(' ')}
                              onClick={canManage ? () => openCellEdit(visualCol.col, rowIds) : undefined}
                            >
                              {renderCellBody(visualCol.col, visualCol.kind, rowObj)}
                            </td>
                          )
                        })}
                        <td>{renderVisibilityCell(rowObj)}</td>
                        {canManage && (
                          <td className="contacts-cell--center">
                            <Space size="mini">
                              <Button size="mini" icon={<IconCopy />} title="在下方复制一行" onClick={() => void handleDuplicateRow(rowObj.id)}>复制</Button>
                              <Button size="mini" icon={<IconArrowUp />} disabled={rawIndex <= 0} onClick={() => void handleMoveRow(rowObj.id, -1)} />
                              <Button size="mini" icon={<IconArrowDown />} disabled={rawIndex < 0 || rawIndex >= allRows.length - 1} onClick={() => void handleMoveRow(rowObj.id, 1)} />
                              <DangerPopconfirm title="确认删除这一行吗？" onOk={() => void handleDeleteRow(rowObj)}>
                                <Button size="mini" status="danger" icon={<IconDelete />} />
                              </DangerPopconfirm>
                            </Space>
                          </td>
                        )}
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </>
      ) : (
        <>
          <div className="contacts-toolbar">
            <Space wrap>
              <Radio.Group type="button" value={peopleScopeFilter} onChange={(val) => setPeopleScopeFilter(val as 'ALL' | 'INTERNAL' | 'VENDOR')}>
                <Radio value="ALL">全部人员 ({allPeople.length})</Radio>
                <Radio value="INTERNAL">我司内部</Radio>
                <Radio value="VENDOR">外包厂商人员</Radio>
              </Radio.Group>
              <Input
                allowClear
                prefix={<IconSearch />}
                placeholder="搜姓名、电话、备注或厂商"
                style={{ width: 240 }}
                value={peopleSearch}
                onChange={(val) => setPeopleSearch(val)}
              />
            </Space>
            {canManage && (
              <Button type="primary" icon={<IconPlus />} onClick={() => void openCreatePerson(false)}>
                新建人员
              </Button>
            )}
          </div>
          <ResizableTable tableId="outsource-contacts-people" growKey="name"
            rowKey="id" columns={peopleColumns} data={filteredPeople} pagination={{ pageSize: 20 }} />
        </>
      )}

      {/* Columns Drawer (outsource:contact_settings) */}
      <Drawer
        width={560}
        title="管理动态列配置"
        visible={columnsDrawerOpen}
        onCancel={() => setColumnsDrawerOpen(false)}
        footer={
          <Space>
            <Button
              type="primary"
              icon={<IconPlus />}
              onClick={() => setColEditor({
                mode: 'CREATE',
                title: '',
                dataType: 'TEXT',
                personDisplay: 'COMBINED',
                phoneTitle: '',
                autoMerge: false,
                vendorVisible: true,
                isGroupColumn: false,
                widthHint: 18,
              })}
            >
              新增一列
            </Button>
            <Button onClick={() => setColumnsDrawerOpen(false)}>关闭</Button>
          </Space>
        }
      >
        {columns.length === 0 ? (
          <Empty description="暂无列配置，点击下方「新增一列」开始创建" />
        ) : (
          <Space direction="vertical" style={{ width: '100%' }} size="medium">
            {columns.map((col, idx) => (
              <div key={col.id} className="contacts-col-card">
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
                  <Space wrap>
                    <strong>{col.title}</strong>
                    <Tag>{DATA_TYPE_LABELS[col.dataType] ?? col.dataType}</Tag>
                    {col.dataType === 'PERSON' && <Tag color="blue">{col.personDisplay === 'SPLIT' ? '拆姓名+电话两列' : '合为一列'}</Tag>}
                    {col.isGroupColumn && <Tag color="arcoblue">分组列</Tag>}
                    {col.autoMerge && <Tag color="green">自动合并</Tag>}
                    {!col.vendorVisible && <Tag color="gray">仅内部</Tag>}
                  </Space>
                  <Space size="mini">
                    <Button size="mini" icon={<IconArrowUp />} disabled={idx === 0} onClick={() => void handleMoveColumn(col.id, -1)} />
                    <Button size="mini" icon={<IconArrowDown />} disabled={idx === columns.length - 1} onClick={() => void handleMoveColumn(col.id, 1)} />
                    <Button
                      size="mini"
                      icon={<IconEdit />}
                      onClick={() => setColEditor({
                        mode: 'EDIT',
                        id: col.id,
                        title: col.title,
                        dataType: col.dataType,
                        personDisplay: col.personDisplay,
                        phoneTitle: col.phoneTitle ?? '',
                        autoMerge: col.autoMerge,
                        vendorVisible: col.vendorVisible,
                        isGroupColumn: col.isGroupColumn,
                        widthHint: col.widthHint,
                      })}
                    >
                      编辑
                    </Button>
                    <DangerPopconfirm title={`确认删除列「${col.title}」及其填报内容？`} onOk={() => void handleDeleteColumn(col)}>
                      <Button size="mini" status="danger" icon={<IconDelete />} />
                    </DangerPopconfirm>
                  </Space>
                </div>
              </div>
            ))}
          </Space>
        )}
      </Drawer>

      {/* Create / Edit Column Modal */}
      <Modal
        title={colEditor?.mode === 'CREATE' ? '新增列' : '编辑列'}
        visible={Boolean(colEditor)}
        onOk={() => void handleSaveColumn()}
        onCancel={() => setColEditor(null)}
        confirmLoading={saving}
      >
        {colEditor && (
          <Space direction="vertical" style={{ width: '100%' }} size="medium">
            <div>
              <div style={{ marginBottom: 4 }}>列标题 *</div>
              <Input value={colEditor.title} placeholder="如：归属端、对应课别、当值工程师" onChange={(v) => setColEditor({ ...colEditor, title: v })} />
            </div>
            <div>
              <div style={{ marginBottom: 4 }}>列类型</div>
              <Select value={colEditor.dataType} onChange={(v) => setColEditor({ ...colEditor, dataType: String(v) })}>
                <Select.Option value="TEXT">单行文本</Select.Option>
                <Select.Option value="MULTILINE">多行文本（职责与要求等）</Select.Option>
                <Select.Option value="PERSON">人员与电话（全局联动）</Select.Option>
              </Select>
            </div>
            {colEditor.dataType === 'PERSON' && (
              <>
                <div>
                  <div style={{ marginBottom: 4 }}>人员显示方式</div>
                  <Radio.Group value={colEditor.personDisplay} onChange={(v) => setColEditor({ ...colEditor, personDisplay: String(v) })}>
                    <Radio value="COMBINED">合为一列（姓名+电话）</Radio>
                    <Radio value="SPLIT">拆为姓名列 + 电话列</Radio>
                  </Radio.Group>
                </div>
                {colEditor.personDisplay === 'SPLIT' && (
                  <div>
                    <div style={{ marginBottom: 4 }}>电话列标题</div>
                    <Input value={colEditor.phoneTitle} placeholder="留空默认「列标题+电话」" onChange={(v) => setColEditor({ ...colEditor, phoneTitle: v })} />
                  </div>
                )}
              </>
            )}
            <Space direction="vertical" size="small">
              <Checkbox checked={colEditor.autoMerge} onChange={(v) => setColEditor({ ...colEditor, autoMerge: v })}>
                连续相同内容自动纵向合并单元格
              </Checkbox>
              <Checkbox checked={colEditor.vendorVisible} onChange={(v) => setColEditor({ ...colEditor, vendorVisible: v })}>
                外包门户与门户导出可见
              </Checkbox>
              <Checkbox checked={colEditor.isGroupColumn} onChange={(v) => setColEditor({ ...colEditor, isGroupColumn: v })}>
                设为分组列（全表至多 1 列，顶部生成筛选标签且禁止其余列跨组合并）
              </Checkbox>
            </Space>
            <div>
              <div style={{ marginBottom: 4 }}>导出 Excel 建议列宽（8–60）</div>
              <InputNumber min={8} max={60} value={colEditor.widthHint} onChange={(v) => setColEditor({ ...colEditor, widthHint: Number(v) || 18 })} />
            </div>
          </Space>
        )}
      </Modal>

      {/* Cell Edit Modal */}
      <Modal
        title={cellModal ? `编辑 · ${cellModal.column.title}` : ''}
        visible={Boolean(cellModal)}
        onOk={() => void handleSaveCell()}
        onCancel={() => setCellModal(null)}
        confirmLoading={saving}
      >
        {cellModal && (
          <Space direction="vertical" style={{ width: '100%' }} size="medium">
            {cellModal.rowIds.length > 1 && (
              <Checkbox checked={cellModal.applyToAll} onChange={(v) => setCellModal({ ...cellModal, applyToAll: v })}>
                同步更新当前合并块的全部 {cellModal.rowIds.length} 行（取消勾选仅改首行并自动拆分）
              </Checkbox>
            )}
            {cellModal.column.dataType === 'TEXT' && (
              <>
                <Input value={cellModal.textValue} onChange={(v) => setCellModal({ ...cellModal, textValue: v })} />
                {cellModal.column.isGroupColumn && groups.length > 0 && (
                  <Space wrap>
                    <span style={{ fontSize: 12, color: 'var(--color-text-3)' }}>快速选用：</span>
                    {groups.map((g) => (
                      <Tag key={g} style={{ cursor: 'pointer' }} onClick={() => setCellModal({ ...cellModal, textValue: g })}>{g}</Tag>
                    ))}
                  </Space>
                )}
              </>
            )}
            {cellModal.column.dataType === 'MULTILINE' && (
              <Input.TextArea rows={6} value={cellModal.textValue} onChange={(v) => setCellModal({ ...cellModal, textValue: v })} />
            )}
            {cellModal.column.dataType === 'PERSON' && (
              <>
                <Space style={{ width: '100%', justifyContent: 'space-between' }}>
                  <span>已选人员（支持多人）：</span>
                  <Button type="text" size="small" icon={<IconPlus />} onClick={() => void openCreatePerson(true)}>
                    主档无此人？新建
                  </Button>
                </Space>
                {cellModal.peopleList.map((entry, idx) => {
                  const p = personById.get(entry.personId)
                  return (
                    <Space key={entry.personId} style={{ width: '100%' }}>
                      <Input
                        style={{ width: 120 }}
                        placeholder="角色前缀(选填)"
                        value={entry.prefixLabel}
                        onChange={(v) => {
                          const next = [...cellModal.peopleList]
                          next[idx] = { ...next[idx], prefixLabel: v }
                          setCellModal({ ...cellModal, peopleList: next })
                        }}
                      />
                      <span style={{ flex: 1 }}>
                        {p ? `${p.name} (${p.phone})${p.scopeType === 'VENDOR' && p.vendorName ? ` [${p.vendorName}]` : ''}` : `人员#${entry.personId}`}
                      </span>
                      <Button
                        size="mini"
                        status="danger"
                        onClick={() => setCellModal({
                          ...cellModal,
                          peopleList: cellModal.peopleList.filter((_, i) => i !== idx),
                        })}
                      >
                        移除
                      </Button>
                    </Space>
                  )
                })}
                <Space style={{ width: '100%' }}>
                  <Select
                    showSearch
                    placeholder="从人员电话库选择并添加"
                    style={{ width: 320 }}
                    filterOption={(input, option) => String(option.props.children).toLowerCase().includes(input.toLowerCase())}
                    value={cellModal.pickedPersonId}
                    onChange={(val) => setCellModal({ ...cellModal, pickedPersonId: val as number })}
                  >
                    {allPeople.filter((p) => p.status === 'ACTIVE').map((p) => (
                      <Select.Option key={p.id} value={p.id}>
                        {`${p.name} · ${p.phone} (${p.scopeType === 'VENDOR' ? `外包:${p.vendorName ?? ''}` : '内部'})`}
                      </Select.Option>
                    ))}
                  </Select>
                  <Button
                    type="primary"
                    onClick={() => {
                      const pid = cellModal.pickedPersonId
                      if (!pid) return
                      if (cellModal.peopleList.some((x) => x.personId === pid)) return
                      setCellModal({
                        ...cellModal,
                        peopleList: [...cellModal.peopleList, { personId: pid, prefixLabel: '' }],
                        pickedPersonId: undefined,
                      })
                    }}
                  >
                    添加
                  </Button>
                </Space>
              </>
            )}
          </Space>
        )}
      </Modal>

      {/* Row / Batch Visibility Modal */}
      <Modal
        title={visModal && visModal.rowIds.length > 1 ? `批量设置给谁看（共 ${visModal.rowIds.length} 行）` : '设置这一行给谁看'}
        visible={Boolean(visModal)}
        onOk={() => void handleSaveVisibility()}
        onCancel={() => setVisModal(null)}
        confirmLoading={saving}
      >
        {visModal && (
          <Space direction="vertical" style={{ width: '100%' }} size="medium">
            <Radio.Group
              direction="vertical"
              value={visModal.visibility}
              onChange={(v) => setVisModal({ ...visModal, visibility: String(v) })}
            >
              <Radio value="ALL_VENDORS">全部外包可见</Radio>
              <Radio value="SPECIFIC_VENDORS">仅勾选的外包厂商可见</Radio>
              <Radio value="INTERNAL_ONLY">仅公司内部可见（对所有外包隐藏）</Radio>
            </Radio.Group>
            {visModal.visibility === 'SPECIFIC_VENDORS' && (
              <Checkbox.Group
                value={visModal.vendorIds}
                onChange={(vals) => setVisModal({ ...visModal, vendorIds: vals as number[] })}
              >
                <Space wrap>
                  {vendors.filter((v) => v.status === 'ACTIVE').map((v) => (
                    <Checkbox key={v.id} value={v.id}>{v.name}</Checkbox>
                  ))}
                </Space>
              </Checkbox.Group>
            )}
          </Space>
        )}
      </Modal>

      {/* Quick Phone Linkage Modal */}
      <Modal
        title="修改人员电话（全局联动同步）"
        visible={Boolean(phoneModal)}
        onOk={() => void handleSaveQuickPhone()}
        onCancel={() => setPhoneModal(null)}
        confirmLoading={saving}
      >
        {phoneModal && (
          <Space direction="vertical" style={{ width: '100%' }} size="medium">
            <Alert
              type="warning"
              content={
                phoneModal.loadingRefs
                  ? '正在查询引用位置…'
                  : `「${phoneModal.person.name}」当前被总表引用在 ${phoneModal.refs.length} 处格位，保存后全表实时同步更新。`
              }
            />
            <Input
              value={phoneModal.phone}
              placeholder="输入新联系电话"
              onChange={(v) => setPhoneModal({ ...phoneModal, phone: v })}
            />
          </Space>
        )}
      </Modal>

      {/* Create / Edit Person Modal */}
      <Modal
        title={personModal?.mode === 'CREATE' ? '新建人员主档' : '编辑人员主档'}
        visible={Boolean(personModal)}
        onOk={() => void handleSavePerson()}
        onCancel={() => setPersonModal(null)}
        confirmLoading={saving}
      >
        {personModal && (
          <Space direction="vertical" style={{ width: '100%' }} size="medium">
            <div>
              <div style={{ marginBottom: 4 }}>人员归属</div>
              <Radio.Group
                value={personModal.scopeType}
                onChange={(v) => setPersonModal({
                  ...personModal,
                  scopeType: v as 'INTERNAL' | 'VENDOR',
                  vendorId: undefined,
                  userId: undefined,
                })}
              >
                <Radio value="INTERNAL">我司内部人员</Radio>
                <Radio value="VENDOR">外包厂商人员（用于防泄密隔离）</Radio>
              </Radio.Group>
            </div>
            {personModal.scopeType === 'VENDOR' ? (
              <div>
                <div style={{ marginBottom: 4 }}>归属外包厂商 *</div>
                <Select
                  placeholder="请选择外包厂商"
                  value={personModal.vendorId}
                  onChange={(v) => setPersonModal({ ...personModal, vendorId: v as number })}
                >
                  {vendors.filter((v) => v.status === 'ACTIVE').map((v) => (
                    <Select.Option key={v.id} value={v.id}>{v.name}</Select.Option>
                  ))}
                </Select>
              </div>
            ) : (
              <div>
                <div style={{ marginBottom: 4 }}>关联系统内部账号（可选）</div>
                <Select
                  allowClear
                  showSearch
                  placeholder="可留空，或选内部账号自动带出姓名"
                  value={personModal.userId}
                  onChange={(v) => {
                    const uid = v as number | undefined
                    const matched = userOptions.find((u) => u.id === uid)
                    setPersonModal({
                      ...personModal,
                      userId: uid,
                      name: personModal.name || matched?.realName || '',
                      email: personModal.email || matched?.email || '',
                    })
                  }}
                >
                  {userOptions.map((u) => (
                    <Select.Option key={u.id} value={u.id}>{`${u.realName} (${u.employeeNo})`}</Select.Option>
                  ))}
                </Select>
              </div>
            )}
            <div>
              <div style={{ marginBottom: 4 }}>姓名 *</div>
              <Input value={personModal.name} onChange={(v) => setPersonModal({ ...personModal, name: v })} />
            </div>
            <div>
              <div style={{ marginBottom: 4 }}>联系电话 *</div>
              <Input value={personModal.phone} onChange={(v) => setPersonModal({ ...personModal, phone: v })} />
            </div>
            <div>
              <div style={{ marginBottom: 4 }}>邮箱（可选）</div>
              <Input value={personModal.email} onChange={(v) => setPersonModal({ ...personModal, email: v })} />
            </div>
            <div>
              <div style={{ marginBottom: 4 }}>职务 / 备注（可选）</div>
              <Input value={personModal.titleHint} onChange={(v) => setPersonModal({ ...personModal, titleHint: v })} />
            </div>
          </Space>
        )}
      </Modal>

      {/* Handover Modal */}
      <Modal
        title={handoverModal ? `一键交接替换 · ${handoverModal.source.name}` : ''}
        visible={Boolean(handoverModal)}
        onOk={() => void handleSaveHandover()}
        onCancel={() => setHandoverModal(null)}
        confirmLoading={saving}
      >
        {handoverModal && (
          <Space direction="vertical" style={{ width: '100%' }} size="medium">
            <div>
              <div style={{ marginBottom: 4 }}>选择同归属接替人 *</div>
              <Select
                showSearch
                value={handoverModal.targetPersonId}
                onChange={(v) => setHandoverModal({ ...handoverModal, targetPersonId: v as number })}
              >
                {allPeople
                  .filter((p) => p.id !== handoverModal.source.id
                    && p.status === 'ACTIVE'
                    && p.scopeType === handoverModal.source.scopeType
                    && p.vendorId === handoverModal.source.vendorId)
                  .map((p) => (
                    <Select.Option key={p.id} value={p.id}>{`${p.name} (${p.phone})`}</Select.Option>
                  ))}
              </Select>
            </div>
            <Checkbox.Group
              direction="vertical"
              value={handoverModal.selectedRowIds}
              onChange={(vals) => setHandoverModal({ ...handoverModal, selectedRowIds: vals as number[] })}
            >
              {handoverModal.refs.map((r) => (
                <Checkbox key={`${r.rowId}-${r.columnId}`} value={r.rowId}>
                  {`第 ${r.rowIndex} 行${r.groupValue ? `（${r.groupValue}）` : ''} · ${r.columnTitle}`}
                </Checkbox>
              ))}
            </Checkbox.Group>
          </Space>
        )}
      </Modal>

      {/* Person References Modal */}
      <Modal
        title={refsModal ? `${refsModal.person.name} 的总表引用位置` : ''}
        visible={Boolean(refsModal)}
        footer={<Button onClick={() => setRefsModal(null)}>关闭</Button>}
        onCancel={() => setRefsModal(null)}
      >
        {refsModal && (
          refsModal.refs.length === 0 ? (
            <Empty description="当前未被任何总表单元格引用" />
          ) : (
            <Space direction="vertical" style={{ width: '100%' }}>
              {refsModal.refs.map((r) => (
                <div key={`${r.rowId}-${r.columnId}`}>
                  {`第 ${r.rowIndex} 行${r.groupValue ? `（${r.groupValue}）` : ''} · [${r.columnTitle}]${r.prefixLabel ? ` 前缀：${r.prefixLabel}` : ''}`}
                </div>
              ))}
            </Space>
          )
        )}
      </Modal>

      {/* Excel Import Modal */}
      <Modal
        style={{ width: 760 }}
        title="Excel 导入对接联络表"
        visible={Boolean(importModal)}
        onOk={() => void handleConfirmImport()}
        okText="确认导入"
        okButtonProps={{
          disabled: !importModal?.preview
            || importModal.parsing
            || importModal.preview.invalid > 0
            || importModal.preview.ready === 0
            || (importModal.preview.phoneChangeCount > 0 && !importModal.confirmPhoneUpdates),
        }}
        onCancel={() => {
          if (saving) return
          ++importPreviewSeq.current
          setImportModal(null)
        }}
        confirmLoading={saving}
      >
        {importModal && (
          <Space direction="vertical" style={{ width: '100%' }} size="medium">
            <Space wrap>
              <span>导入模式：</span>
              <Radio.Group
                value={importModal.mode}
                disabled={importModal.parsing || saving}
                onChange={(val) => {
                  const nextMode = val as 'APPEND' | 'REPLACE_ALL' | 'REPLACE_GROUPS'
                  if (importModal.parsedRows.length > 0) {
                    void runImportPreview(nextMode, importModal.parsedRows, importModal.overrides, importModal.fileName)
                  } else {
                    setImportModal({ ...importModal, mode: nextMode })
                  }
                }}
              >
                <Radio value="APPEND">末尾追加</Radio>
                {grid.groupColumn && <Radio value="REPLACE_GROUPS">{`按「${grid.groupColumn.title}」覆盖`}</Radio>}
                <Radio value="REPLACE_ALL">整表覆盖</Radio>
              </Radio.Group>
            </Space>

            <Space>
              <input
                ref={importFileRef}
                type="file"
                accept=".xlsx"
                style={{ display: 'none' }}
                onChange={(e) => {
                  const file = e.target.files?.[0]
                  if (file) void handlePickImportFile(file)
                  e.target.value = ''
                }}
              />
              <Button icon={<IconUpload />} loading={importModal.parsing} disabled={saving} onClick={() => importFileRef.current?.click()}>
                选择 .xlsx 文件
              </Button>
              {importModal.fileName && <span>{importModal.fileName}</span>}
              {!importModal.preview && !importModal.parsing && importModal.parsedRows.length > 0 && (
                <Button onClick={() => void runImportPreview(importModal.mode, importModal.parsedRows,
                  importModal.overrides, importModal.fileName)}>重新预览</Button>
              )}
            </Space>

            {importModal.preview && (
              <>
                <Space wrap>
                  <Tag color="green">待写入：{importModal.preview.ready}</Tag>
                  {importModal.mode !== 'APPEND' && <Tag color="orange">待覆盖：{importModal.preview.replaceCount}</Tag>}
                  <Tag color="gray">忽略范例：{importModal.preview.skipped}</Tag>
                  <Tag color={importModal.preview.invalid > 0 ? 'red' : 'gray'}>异常：{importModal.preview.invalid}</Tag>
                </Space>

                <ResizableTable
                  tableId="outsource-contacts-import-preview"
                  growKey="message"
                  size="small"
                  pagination={false}
                  scroll={{ y: 200 }}
                  rowKey="rowNumber"
                  data={importModal.preview.rows}
                  columns={[
                    { title: '行号', dataIndex: 'rowNumber', width: 70 },
                    {
                      title: '校验',
                      dataIndex: 'status',
                      width: 85,
                      render: (s) => <Tag color={s === 'READY' ? 'green' : s === 'SKIPPED' ? 'gray' : 'red'}>{s}</Tag>,
                    },
                    { title: '可见范围', dataIndex: 'visibility', width: 140, render: (_, r) => r.vendorNames.length ? r.vendorNames.join('、') : (VISIBILITY_LABELS[r.visibility] ?? r.visibility) },
                    { title: '分组值', dataIndex: 'groupValue', width: 110, render: (v) => v ?? '—' },
                    { title: '说明', dataIndex: 'message', render: (m) => m ?? '校验通过' },
                  ]}
                />

                {importModal.preview.peoplePlans.some((p) => p.action !== 'REUSE') && (
                  <div style={{ maxHeight: 160, overflowY: 'auto', border: '1px solid var(--color-border-2)', padding: 8, borderRadius: 6 }}>
                    <div style={{ fontWeight: 600, marginBottom: 6 }}>人员匹配与联动明细：</div>
                    <Space direction="vertical" style={{ width: '100%' }}>
                      {importModal.preview.peoplePlans.filter((p) => p.action !== 'REUSE').map((p) => (
                        <Space key={p.matchKey} wrap>
                          <Tag color={p.action === 'CREATE' ? 'arcoblue' : 'orange'}>
                            {p.action === 'CREATE' ? '将新建人员' : '电话变更'}
                          </Tag>
                          <span>{p.name} ({p.existingPhone ? `${p.existingPhone} → ` : ''}{p.phone})</span>
                          {p.canMarkVendor && p.suggestedVendorId && p.action === 'CREATE' && (
                            <Checkbox
                              disabled={saving}
                              checked={importModal.overrides[p.matchKey] === p.suggestedVendorId}
                              onChange={(checked) => {
                                const nextOverrides = {
                                  ...importModal.overrides,
                                  [p.matchKey]: checked ? p.suggestedVendorId! : null,
                                }
                                void runImportPreview(importModal.mode, importModal.parsedRows, nextOverrides, importModal.fileName)
                              }}
                            >
                              {`标记为【${p.suggestedVendorName}】外包联系人`}
                            </Checkbox>
                          )}
                        </Space>
                      ))}
                    </Space>
                  </div>
                )}

                {importModal.preview.phoneChangeCount > 0 && (
                  <Alert
                    type="warning"
                    content={
                      <Checkbox
                        disabled={saving}
                        checked={importModal.confirmPhoneUpdates}
                        onChange={(v) => setImportModal({ ...importModal, confirmPhoneUpdates: v })}
                      >
                        {`检测到 ${importModal.preview.phoneChangeCount} 位已有同名人员电话发生变化，勾选确认导入后将全局联动更新全表引用`}
                      </Checkbox>
                    }
                  />
                )}
              </>
            )}
          </Space>
        )}
      </Modal>
    </Card>
  )
}
