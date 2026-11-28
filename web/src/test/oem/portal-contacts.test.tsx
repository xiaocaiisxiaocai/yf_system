import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import type { OemApi } from '../../oem/api/OemApi'
import { OemProvider } from '../../oem/OemContext'
import PortalContactsPage from '../../oem/pages/PortalContactsPage'
import * as downloads from '../../utils/saveBlob'

const portalColumns = [
  { id: 1, colKey: 'grp', title: '归属端', dataType: 'TEXT', personDisplay: 'COMBINED', phoneTitle: null, autoMerge: true, isGroupColumn: true, sortNo: 10, widthHint: 16 },
  { id: 2, colKey: 'unit', title: '对应课别', dataType: 'TEXT', personDisplay: 'COMBINED', phoneTitle: null, autoMerge: true, isGroupColumn: false, sortNo: 20, widthHint: 18 },
  { id: 3, colKey: 'mgr', title: '对应主管', dataType: 'PERSON', personDisplay: 'SPLIT', phoneTitle: '主管电话', autoMerge: true, isGroupColumn: false, sortNo: 30, widthHint: 16 },
]

const portalRows = [
  {
    id: 1, sortNo: 10,
    values: { grp: '研发端', unit: '机构一课' },
    people: { mgr: [{ personId: 10, prefixLabel: null, name: '主管甲', phone: '13800000001', email: null }] },
  },
  {
    id: 2, sortNo: 20,
    values: { grp: '研发端', unit: '电控工令' },
    people: { mgr: [{ personId: 11, prefixLabel: null, name: '主管乙', phone: '13800000002', email: null }] },
  },
  {
    id: 3, sortNo: 30,
    values: { grp: '采购端', unit: '外协采购' },
    people: { mgr: [{ personId: 12, prefixLabel: null, name: '采购丙', phone: '13800000003', email: null }] },
  },
]

describe('PortalContactsPage', () => {
  it('renders merged read-only table, group filter, search, and exports vendor workbook', async () => {
    const user = userEvent.setup()
    const contactMatrix = vi.fn<OemApi['contactMatrix']>().mockResolvedValue({ columns: portalColumns, rows: portalRows })
    const blob = new Blob(['xlsx'])
    const contactMatrixExport = vi.fn<OemApi['contactMatrixExport']>().mockResolvedValue(blob)
    const save = vi.spyOn(downloads, 'saveBlob').mockImplementation(() => undefined)

    render(
      <MemoryRouter>
        <OemProvider value={{
          api: { contactMatrix, contactMatrixExport } as unknown as OemApi,
          realm: 'oem', base: '/oem-portal', permissions: new Set(), userId: 5, queryScope: ['portal', 5],
        }}>
          <PortalContactsPage />
        </OemProvider>
      </MemoryRouter>,
    )

    const rdCell = await screen.findByText('研发端', { selector: 'td span' })
    expect(rdCell.closest('td')).toHaveAttribute('rowspan', '2')
    expect(screen.getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['归属端', '对应课别', '对应主管', '主管电话'])
    expect(screen.queryByRole('button', { name: /新增|编辑|删除|管理列|给谁看/ })).not.toBeInTheDocument()

    // Filter by group pill 采购端
    await user.click(screen.getByText('采购端', { selector: 'span.arco-radio-button-inner' }))
    expect(screen.queryByText('机构一课')).not.toBeInTheDocument()
    expect(screen.getByText('外协采购')).toBeInTheDocument()

    // Export Excel
    await user.click(screen.getByRole('button', { name: '导出 Excel' }))
    expect(contactMatrixExport).toHaveBeenCalledTimes(1)
    await waitFor(() => expect(save).toHaveBeenCalledWith(blob, expect.stringMatching(/^对接联络表_\d{8}\.xlsx$/)))
    save.mockRestore()
  })
})
