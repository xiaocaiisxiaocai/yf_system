import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock('../../api/client', () => ({ default: { get: mocks.get } }))
vi.mock('../../../generated/docx-viewer.html?raw', () => ({
  default: '<!doctype html><script nonce="YF_VIEWER_NONCE"></script>',
}))

import DocxPreview from '../../components/DocxPreview'

const zipBytes = () => new Uint8Array([0x50, 0x4b, 3, 4]).buffer
// Bytes are released only after the frame's postMessage is observed, as in the PPTX preview tests.
let release: Array<() => void> = []
function gated<T>(value: () => T) {
  return () => new Promise<T>(resolve => { release.push(() => resolve(value())) })
}

function message(source: MessageEventSource | null, data: Record<string, unknown>) {
  window.dispatchEvent(new MessageEvent('message', { source, data }))
}

async function loadedFrame() {
  const frame = await screen.findByTitle('Word 预览内容') as HTMLIFrameElement
  const postMessage = vi.spyOn(frame.contentWindow!, 'postMessage').mockImplementation(() => undefined)
  fireEvent.load(frame)
  release.splice(0).forEach(run => run())
  await waitFor(() => expect(postMessage).toHaveBeenCalledOnce())
  const payload = postMessage.mock.calls[0][0] as { channel: string; type: string }
  return { frame, postMessage, channel: payload.channel }
}

describe('Word preview', () => {
  beforeEach(() => {
    release = []
    mocks.get.mockReset()
    mocks.get.mockImplementation(gated(() => ({ data: zipBytes() })))
  })
  afterEach(() => {
    document.querySelectorAll('[data-test-toolbar]').forEach(node => node.remove())
  })

  it('keeps authenticated bytes in a sandbox and accepts status only from its frame and channel', async () => {
    const toolbar = document.createElement('div')
    toolbar.dataset.testToolbar = 'true'
    document.body.append(toolbar)
    const user = userEvent.setup()
    render(<DocxPreview fileId={41} toolbarContainer={toolbar} />)
    const { frame, postMessage, channel } = await loadedFrame()

    expect(mocks.get).toHaveBeenCalledWith('/files/41/content', expect.objectContaining({ responseType: 'arraybuffer' }))
    expect(frame).toHaveAttribute('sandbox', 'allow-scripts')
    expect(frame).toHaveAttribute('referrerpolicy', 'no-referrer')
    expect(frame.srcdoc).not.toContain('YF_VIEWER_NONCE')
    expect(postMessage.mock.calls[0][0]).toMatchObject({ type: 'docx:load', channel })

    message(window, { type: 'docx:rendered', channel, scale: 0.8, zoom: 'fit' })
    message(frame.contentWindow, { type: 'docx:rendered', channel: 'wrong', scale: 0.8, zoom: 'fit' })
    expect(frame).toHaveStyle({ visibility: 'hidden' })
    message(frame.contentWindow, { type: 'docx:rendered', channel, scale: 0.8, zoom: 'fit' })
    await waitFor(() => expect(frame).toHaveStyle({ visibility: 'visible' }))

    const controls = within(toolbar).getByRole('group', { name: 'Word 阅读控制' })
    await user.click(within(controls).getByRole('button', { name: '放大 Word 文档' }))
    expect(postMessage.mock.calls.at(-1)?.[0]).toMatchObject({ type: 'docx:command', command: 'zoom', channel, value: 1 })
    message(frame.contentWindow, { type: 'docx:scale-changed', channel, scale: 'not-a-number' })
    await user.click(within(controls).getByRole('button', { name: '缩小 Word 文档' }))
    expect(postMessage.mock.calls.at(-1)?.[0]).toMatchObject({ value: 0.64 })
  })

  it('reads bytes from a supplied loader instead of the collaboration endpoint', async () => {
    const loadContent = vi.fn(gated(zipBytes))
    render(<DocxPreview fileId={42} loadContent={loadContent} />)
    await loadedFrame()
    expect(loadContent).toHaveBeenCalledWith(expect.any(AbortSignal))
    expect(mocks.get).not.toHaveBeenCalled()
  })

  it('refuses a legacy .doc renamed to .docx without posting it to the renderer', async () => {
    mocks.get.mockImplementation(gated(() => ({ data: new Uint8Array([0xd0, 0xcf, 0x11, 0xe0]).buffer })))
    render(<DocxPreview fileId={43} />)
    const frame = await screen.findByTitle('Word 预览内容') as HTMLIFrameElement
    const postMessage = vi.spyOn(frame.contentWindow!, 'postMessage').mockImplementation(() => undefined)
    fireEvent.load(frame)
    release.splice(0).forEach(run => run())
    expect(await screen.findByRole('alert')).toHaveTextContent('不是有效的 DOCX 文档')
    expect(postMessage).not.toHaveBeenCalled()
  })

  it('shows the renderer failure and reloads on retry', async () => {
    const user = userEvent.setup()
    render(<DocxPreview fileId={44} />)
    const { frame, channel } = await loadedFrame()
    message(frame.contentWindow, { type: 'docx:error', channel })
    expect(await screen.findByRole('alert')).toHaveTextContent('Word 预览失败')
    await user.click(screen.getByRole('button', { name: '重试 Word 预览' }))
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2))
  })
})
