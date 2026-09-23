import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../../../api/client', () => ({ default: { get: mocks.get } }))
vi.mock('../../../../generated/excel-viewer.html?raw', () => ({ default: '<!doctype html><title>viewer</title>' }))

import ExcelPreview from '../../../components/ExcelPreview'

type PostedMessage = { type: string; channel: string; buffer: ArrayBuffer }

function installFrameWindow(frame: HTMLIFrameElement, postMessage = vi.fn()) {
  const contentWindow = { postMessage }
  Object.defineProperty(frame, 'contentWindow', { configurable: true, value: contentWindow })
  return contentWindow
}

describe('Excel preview request and frame boundary', () => {
  beforeEach(() => {
    mocks.get.mockReset()
  })

  it('Excel preview aborts its content request and never posts late bytes after unmount', async () => {
    let resolveDownload!: (value: { data: ArrayBuffer }) => void
    let signal: AbortSignal | undefined
    const download = new Promise<{ data: ArrayBuffer }>((resolve) => { resolveDownload = resolve })
    mocks.get.mockImplementation((_url: string, options: { signal: AbortSignal }) => {
      signal = options.signal
      return download
    })
    const { unmount } = render(<ExcelPreview fileId={7} />)
    const frame = screen.getByTitle('Excel 预览内容') as HTMLIFrameElement
    const postMessage = vi.fn()
    installFrameWindow(frame, postMessage)

    fireEvent.load(frame)
    unmount()
    await act(async () => {
      resolveDownload({ data: new Uint8Array([0x50, 0x4b]).buffer })
      await download
    })

    expect(signal?.aborted).toBe(true)
    expect(postMessage).not.toHaveBeenCalled()
  })

  it('Excel preview waits for its frame and accepts status only from that frame and channel', async () => {
    let resolveDownload!: (value: { data: ArrayBuffer }) => void
    mocks.get.mockReturnValue(new Promise((resolve) => { resolveDownload = resolve }))
    render(<ExcelPreview fileId={9} />)
    const frame = screen.getByTitle('Excel 预览内容') as HTMLIFrameElement
    const postMessage = vi.fn()
    const contentWindow = installFrameWindow(frame, postMessage)

    expect(postMessage).not.toHaveBeenCalled()
    expect(frame).toHaveAttribute('sandbox', 'allow-scripts')
    await act(async () => {
      resolveDownload({ data: new Uint8Array([0x50, 0x4b, 3, 4]).buffer })
    })
    if (postMessage.mock.calls.length === 0) fireEvent.load(frame)
    await waitFor(() => expect(postMessage).toHaveBeenCalledOnce())
    const sent = postMessage.mock.calls[0][0] as PostedMessage

    fireEvent(window, new MessageEvent('message', {
      source: {} as Window,
      data: { type: 'excel:rendered', channel: sent.channel },
    }))
    fireEvent(window, new MessageEvent('message', {
      source: contentWindow as unknown as Window,
      data: { type: 'excel:rendered', channel: 'wrong' },
    }))
    expect(frame).toHaveStyle({ visibility: 'hidden' })

    fireEvent(window, new MessageEvent('message', {
      source: contentWindow as unknown as Window,
      data: { type: 'excel:rendered', channel: sent.channel },
    }))
    await waitFor(() => expect(frame).toHaveStyle({ visibility: 'visible' }))
  })

  it('Excel preview retry fetches a new buffer and waits for the replacement frame', async () => {
    const resolvers: Array<(value: { data: ArrayBuffer }) => void> = []
    mocks.get.mockImplementation(() => new Promise((resolve) => { resolvers.push(resolve) }))
    render(<ExcelPreview fileId={10} />)
    const firstFrame = screen.getByTitle('Excel 预览内容') as HTMLIFrameElement
    const firstPost = vi.fn()
    const firstWindow = installFrameWindow(firstFrame, firstPost)
    await act(async () => {
      resolvers[0]({ data: new Uint8Array([0x50, 0x4b, 3, 4]).buffer })
    })
    if (firstPost.mock.calls.length === 0) fireEvent.load(firstFrame)
    await waitFor(() => expect(firstPost).toHaveBeenCalledOnce())
    const first = firstPost.mock.calls[0][0] as PostedMessage

    fireEvent(window, new MessageEvent('message', {
      source: firstWindow as unknown as Window,
      data: { type: 'excel:error', channel: first.channel },
    }))
    await screen.findByRole('alert')
    fireEvent.click(screen.getByRole('button', { name: '重试预览' }))

    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2))
    const secondFrame = screen.getByTitle('Excel 预览内容') as HTMLIFrameElement
    const secondPost = vi.fn()
    installFrameWindow(secondFrame, secondPost)
    expect(secondPost).not.toHaveBeenCalled()
    await act(async () => {
      resolvers[1]({ data: new Uint8Array([0x50, 0x4b, 3, 4]).buffer })
    })
    if (secondPost.mock.calls.length === 0) fireEvent.load(secondFrame)
    await waitFor(() => expect(secondPost).toHaveBeenCalledOnce())
    expect((secondPost.mock.calls[0][0] as PostedMessage).buffer).not.toBe(first.buffer)
  })
})
