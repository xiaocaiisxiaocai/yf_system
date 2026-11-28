import { describe, expect, it } from 'vitest'
import { buildMergedContactGrid, getDistinctGroups, type ContactColumnShape, type ContactRowShape } from '../../outsource/contactsMerge'

const columns: ContactColumnShape[] = [
  { id: 1, colKey: 'group', title: '归属端', dataType: 'TEXT', personDisplay: 'COMBINED', autoMerge: true, isGroupColumn: true, sortNo: 10 },
  { id: 2, colKey: 'unit', title: '对应课别', dataType: 'TEXT', personDisplay: 'COMBINED', autoMerge: true, isGroupColumn: false, sortNo: 20 },
  { id: 3, colKey: 'eng', title: '当值工程师', dataType: 'PERSON', personDisplay: 'COMBINED', autoMerge: false, isGroupColumn: false, sortNo: 30 },
  { id: 4, colKey: 'mgr', title: '课别主管', dataType: 'PERSON', personDisplay: 'SPLIT', phoneTitle: '主管电话', autoMerge: true, isGroupColumn: false, sortNo: 40 },
]

const rows: ContactRowShape[] = [
  {
    id: 10, sortNo: 10,
    values: { group: '研发端', unit: '机构一课' },
    people: { eng: [], mgr: [{ personId: 1, name: '主管甲', phone: '13800000001' }] },
  },
  {
    id: 11, sortNo: 20,
    values: { group: '研发端', unit: '电控工令' },
    people: { eng: [{ personId: 2, name: '工程师1', phone: '13800000011' }], mgr: [{ personId: 3, name: '主管乙', phone: '13800000002' }] },
  },
  {
    id: 12, sortNo: 30,
    values: { group: '研发端', unit: '电控工令' },
    people: { eng: [{ personId: 4, name: '工程师2', phone: '13800000012' }], mgr: [{ personId: 3, name: '主管乙', phone: '13800000002' }] },
  },
  {
    id: 13, sortNo: 40,
    values: { group: '生管端', unit: '电控工令' },
    people: { eng: [], mgr: [{ personId: 3, name: '主管乙', phone: '13800000002' }] },
  },
]

describe('contactsMerge', () => {
  it('expands SPLIT columns, merges identical non-empty values within group boundaries, and never merges empty cells', () => {
    const res = buildMergedContactGrid(columns, rows)
    expect(res.visualCols.map((c) => c.title)).toEqual(['归属端', '对应课别', '当值工程师', '课别主管', '主管电话'])
    expect(getDistinctGroups(rows, columns)).toEqual(['研发端', '生管端'])

    // 研发端 spans rows 0..2 (rowSpan=3)
    expect(res.grid[0][0].rowSpan).toBe(3)
    expect(res.grid[0][0].rowIds).toEqual([10, 11, 12])
    expect(res.grid[1][0].skip).toBe(true)

    // 电控工令 spans rows 1..2 inside 研发端, does NOT merge across boundary into 生管端 (row 3)!
    expect(res.grid[1][1].rowSpan).toBe(2)
    expect(res.grid[1][1].rowIds).toEqual([11, 12])
    expect(res.grid[2][1].skip).toBe(true)
    expect(res.grid[3][1].skip).toBe(false)
    expect(res.grid[3][1].rowSpan).toBe(1)

    // SPLIT column mgr name and phone both merge rows 1..2
    expect(res.grid[1][3].rowSpan).toBe(2)
    expect(res.grid[1][4].rowSpan).toBe(2)
    expect(res.grid[2][3].skip).toBe(true)
    expect(res.grid[2][4].skip).toBe(true)

    // Empty eng cells on row 0 and row 3 never merge
    expect(res.grid[0][2].rowSpan).toBe(1)
    expect(res.grid[3][2].rowSpan).toBe(1)
  })

  it('allows no group column to be configured and merges purely by consecutive values', () => {
    const noGroupCols = columns.map((c) => ({ ...c, isGroupColumn: false }))
    const res = buildMergedContactGrid(noGroupCols, rows)
    expect(res.groupColumn).toBeNull()
    expect(getDistinctGroups(rows, noGroupCols)).toEqual([])
    // Without a group column, row 1..3 sharing unit='电控工令' merge continuously!
    expect(res.grid[1][1].rowSpan).toBe(3)
    expect(res.grid[1][1].rowIds).toEqual([11, 12, 13])
  })

  it('filters by groupFilter and keyword before computing spans', () => {
    const res = buildMergedContactGrid(columns, rows, { groupFilter: '研发端', keyword: '13800000011' })
    expect(res.rows.map((r) => r.id)).toEqual([11])
    expect(res.grid[0][0].rowSpan).toBe(1)
  })
})
