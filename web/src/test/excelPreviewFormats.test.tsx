import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../api/client', () => ({ default: { get: mocks.get } }))
vi.mock('../../generated/excel-viewer.html?raw', () => ({ default: '<!doctype html><title>viewer</title>' }))

import ExcelPreview from '../components/ExcelPreview'

const ZIP = [0x50, 0x4b, 3, 4]
const OLE2 = [0xd0, 0xcf, 0x11, 0xe0]

// .xls/.xlsx/.xlsm/.xlsb 同等可预览：OLE2 .xls 与二进制 .xlsb 需先经 SheetJS 转换，.xlsx/.xlsm 直接交给 ExcelJS。
describe('Excel preview workbook formats', () => {
  beforeEach(() => mocks.get.mockReset())

  it.each([
    ['xlsx', ZIP, false],
    ['xlsm', ZIP, false],
    ['XLSB', ZIP, true],
    ['xls', OLE2, true],
    [undefined, ZIP, false],
  ] as const)('ext %s with signature %j asks the viewer to convert: %s', async (ext, signature, convert) => {
    let resolveDownload!: (value: { data: ArrayBuffer }) => void
    mocks.get.mockReturnValue(new Promise((resolve) => { resolveDownload = resolve }))
    render(<ExcelPreview fileId={11} ext={ext} />)
    const frame = screen.getByTitle('Excel 预览内容') as HTMLIFrameElement
    const postMessage = vi.fn()
    Object.defineProperty(frame, 'contentWindow', { configurable: true, value: { postMessage } })
    await act(async () => { resolveDownload({ data: new Uint8Array(signature).buffer }) })
    if (postMessage.mock.calls.length === 0) fireEvent.load(frame)
    await waitFor(() => expect(postMessage).toHaveBeenCalledOnce())
    expect(postMessage.mock.calls[0][0]).toMatchObject({ type: 'excel:load', xls: convert })
  })
})
