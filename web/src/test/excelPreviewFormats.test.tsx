import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../api/client', () => ({ default: { get: mocks.get } }))
vi.mock('../../generated/excel-viewer.html?raw', () => ({ default: '<!doctype html><title>viewer</title>' }))

import ExcelPreview from '../components/ExcelPreview'

const ZIP = [0x50, 0x4b, 3, 4]
const OLE2 = [0xd0, 0xcf, 0x11, 0xe0]

// .xls/.xlsx/.xlsm/.xlsb 同等可预览：OLE2 .xls、二进制 .xlsb 和启用宏的 .xlsm 需先经 SheetJS 转换，.xlsx 直接交给 ExcelJS。
describe('Excel preview workbook formats', () => {
  beforeEach(() => mocks.get.mockReset())

  it.each([
    ['xlsx', ZIP, false],
    ['xlsm', ZIP, true],
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

  it('does not re-invoke loadContent or re-post when rerendered with a new loadContent reference for the same fileId', async () => {
    let resolveDownload!: (value: ArrayBuffer) => void
    const loader1 = vi.fn(() => new Promise<ArrayBuffer>(resolve => { resolveDownload = resolve }))
    const loader2 = vi.fn(() => Promise.resolve(new Uint8Array(ZIP).buffer))
    const view = render(<ExcelPreview fileId={21} ext="xlsx" loadContent={() => loader1()} />)
    const frame = screen.getByTitle('Excel 预览内容') as HTMLIFrameElement
    const postMessage = vi.fn()
    Object.defineProperty(frame, 'contentWindow', { configurable: true, value: { postMessage } })
    fireEvent.load(frame)
    await act(async () => { resolveDownload(new Uint8Array(ZIP).buffer) })
    await waitFor(() => expect(postMessage).toHaveBeenCalledOnce())
    const channel = (postMessage.mock.calls[0][0] as { channel: string }).channel

    window.dispatchEvent(new MessageEvent('message', {
      source: frame.contentWindow,
      data: { type: 'excel:rendered', channel },
    }))
    await waitFor(() => expect(frame).toHaveStyle({ visibility: 'visible' }))

    view.rerender(<ExcelPreview fileId={21} ext="xlsx" loadContent={() => loader2()} />)
    await act(async () => { await Promise.resolve() })
    expect(loader1).toHaveBeenCalledOnce()
    expect(loader2).not.toHaveBeenCalled()
    expect(postMessage).toHaveBeenCalledOnce()
    expect(frame).toHaveStyle({ visibility: 'visible' })
  })

  it('remounts the iframe with a fresh channel on retry after a render error', async () => {
    const pendingDownloads: Array<(value: ArrayBuffer) => void> = []
    const loader = vi.fn(() => new Promise<ArrayBuffer>(resolve => { pendingDownloads.push(resolve) }))
    render(<ExcelPreview fileId={22} ext="xlsx" loadContent={loader} />)
    const firstFrame = screen.getByTitle('Excel 预览内容') as HTMLIFrameElement
    const firstPostMessage = vi.fn()
    Object.defineProperty(firstFrame, 'contentWindow', { configurable: true, value: { postMessage: firstPostMessage } })
    await act(async () => { pendingDownloads.shift()?.(new Uint8Array(ZIP).buffer) })
    if (firstPostMessage.mock.calls.length === 0) fireEvent.load(firstFrame)
    await waitFor(() => expect(firstPostMessage).toHaveBeenCalledOnce())
    const firstChannel = (firstPostMessage.mock.calls[0][0] as { channel: string }).channel

    window.dispatchEvent(new MessageEvent('message', {
      source: firstFrame.contentWindow,
      data: { type: 'excel:error', channel: firstChannel },
    }))
    const retryButton = await screen.findByRole('button', { name: '重试预览' })
    fireEvent.click(retryButton)

    await waitFor(() => expect(loader).toHaveBeenCalledTimes(2))
    const secondFrame = screen.getByTitle('Excel 预览内容') as HTMLIFrameElement
    expect(secondFrame).not.toBe(firstFrame)
    const secondPostMessage = vi.fn()
    Object.defineProperty(secondFrame, 'contentWindow', { configurable: true, value: { postMessage: secondPostMessage } })
    await act(async () => { pendingDownloads.shift()?.(new Uint8Array(ZIP).buffer) })
    if (secondPostMessage.mock.calls.length === 0) fireEvent.load(secondFrame)
    await waitFor(() => expect(secondPostMessage).toHaveBeenCalledOnce())
    const secondChannel = (secondPostMessage.mock.calls[0][0] as { channel: string }).channel
    expect(secondChannel).not.toBe(firstChannel)
  })
})
