import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { readFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({ get: vi.fn() }))
const defaultRequests: Array<ReturnType<typeof deferred<{ data: ArrayBuffer }>>> = []

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get },
}))

import PptxPreview from '../../../components/PptxPreview'

function pptxBytes() {
  return new Uint8Array([0x50, 0x4b, 3, 4]).buffer
}

function message(source: MessageEventSource | null, data: Record<string, unknown>) {
  window.dispatchEvent(new MessageEvent('message', { source, data }))
}

async function loadedFrame() {
  const frame = await screen.findByTitle('PPTX 预览内容') as HTMLIFrameElement
  const postMessage = vi.spyOn(frame.contentWindow!, 'postMessage').mockImplementation(() => undefined)
  fireEvent.load(frame)
  defaultRequests.at(-1)?.resolve({ data: pptxBytes() })
  await waitFor(() => expect(postMessage).toHaveBeenCalledOnce())
  const payload = postMessage.mock.calls[0][0] as { channel: string; type: string }
  return { frame, postMessage, channel: payload.channel }
}

describe('PPTX preview', () => {
  beforeEach(() => {
    defaultRequests.length = 0
    mocks.get.mockImplementation(() => {
      const request = deferred<{ data: ArrayBuffer }>()
      defaultRequests.push(request)
      return request.promise
    })
  })

  afterEach(() => {
    document.querySelectorAll('[data-test-toolbar]').forEach(node => node.remove())
  })

  it('PPTX preview keeps authenticated bytes in a sandbox and drives the portal toolbar by channel messages', async () => {
    const toolbar = document.createElement('div')
    toolbar.dataset.testToolbar = 'true'
    document.body.append(toolbar)
    const user = userEvent.setup()
    const view = render(<PptxPreview fileId={41} toolbarContainer={toolbar} />)
    const { frame, postMessage, channel } = await loadedFrame()

    expect(mocks.get).toHaveBeenCalledWith('/files/41/content', expect.objectContaining({ responseType: 'arraybuffer' }))
    expect(frame).toHaveAttribute('sandbox', 'allow-scripts')
    expect(frame).toHaveAttribute('referrerpolicy', 'no-referrer')
    expect(frame.srcdoc).not.toContain('YF_VIEWER_NONCE')
    expect(frame).toHaveStyle({ visibility: 'hidden' })
    expect(postMessage.mock.calls[0][0]).toMatchObject({ type: 'pptx:load', channel })

    message(window, { type: 'pptx:rendered', channel, pageCount: 3, pageNumber: 1, scale: 0.8 })
    expect(frame).toHaveStyle({ visibility: 'hidden' })
    message(frame.contentWindow, { type: 'pptx:rendered', channel: 'wrong', pageCount: 3 })
    expect(frame).toHaveStyle({ visibility: 'hidden' })
    message(frame.contentWindow, { type: 'pptx:rendered', channel, pageCount: 3, pageNumber: 1, scale: 0.8, zoom: 'page' })

    await waitFor(() => expect(frame).toHaveStyle({ visibility: 'visible' }))
    expect(within(toolbar).getByRole('group', { name: 'PPTX 阅读控制' })).toBeVisible()
    message(frame.contentWindow, { type: 'pptx:page-changed', channel, pageNumber: Number.POSITIVE_INFINITY })
    message(frame.contentWindow, { type: 'pptx:scale-changed', channel, scale: 'not-a-number' })
    expect(within(toolbar).getByRole('spinbutton', { name: 'PPTX 幻灯片编号' })).toHaveValue('1')

    await user.click(within(toolbar).getByRole('button', { name: '下一张幻灯片' }))
    expect(postMessage.mock.calls.at(-1)?.[0]).toMatchObject({ type: 'pptx:command', command: 'page', value: 2 })
    await user.click(within(toolbar).getByRole('combobox', { name: 'PPTX 缩放' }))
    fireEvent.click(await screen.findByText('适合宽度'))
    expect(postMessage.mock.calls.at(-1)?.[0]).toMatchObject({ type: 'pptx:command', command: 'zoom', value: 'fit' })
    await user.click(within(toolbar).getByRole('button', { name: '重置 PPTX 缩放' }))
    expect(postMessage.mock.calls.at(-1)?.[0]).toMatchObject({ type: 'pptx:command', command: 'zoom', value: 'page' })
    expect(view.container).not.toHaveTextContent('下载')

    const signal = (mocks.get.mock.calls[0][1] as { signal: AbortSignal }).signal
    view.unmount()
    expect(signal.aborted).toBe(true)
  })

  it('PPTX preview rejects non-finite render status values from the isolated frame', async () => {
    render(<PptxPreview fileId={42} />)
    const { frame, channel } = await loadedFrame()

    message(frame.contentWindow, { type: 'pptx:rendered', channel, pageCount: 'NaN', pageNumber: 1, scale: 1 })
    expect(await screen.findByRole('alert')).toHaveTextContent('PPTX 预览失败')
    expect(screen.getByRole('alert')).toHaveTextContent('无效结果')
  })

  it('PPTX preview aborts stale requests and ignores bytes that resolve after a file switch', async () => {
    const requests: Array<ReturnType<typeof deferred<{ data: ArrayBuffer }>> & { signal: AbortSignal }> = []
    mocks.get.mockImplementation((_url, options) => {
      const request = { ...deferred<{ data: ArrayBuffer }>(), signal: options.signal }
      requests.push(request)
      return request.promise
    })
    const view = render(<PptxPreview fileId={1} />)
    const frame = await screen.findByTitle('PPTX 预览内容') as HTMLIFrameElement
    const postMessage = vi.spyOn(frame.contentWindow!, 'postMessage').mockImplementation(() => undefined)
    fireEvent.load(frame)
    view.rerender(<PptxPreview fileId={2} />)

    expect(requests[0].signal.aborted).toBe(true)
    requests[0].resolve({ data: pptxBytes() })
    await requests[0].promise
    await Promise.resolve()
    expect(postMessage).not.toHaveBeenCalled()
    view.unmount()
    expect(requests[1].signal.aborted).toBe(true)
  })

  it('PPTX preview retry owns a fresh request and iframe channel after a render error', async () => {
    const user = userEvent.setup()
    render(<PptxPreview fileId={9} />)
    const first = await loadedFrame()
    message(first.frame.contentWindow, { type: 'pptx:error', channel: first.channel })
    await user.click(await screen.findByRole('button', { name: '重试 PPTX 预览' }))

    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2))
    const second = await loadedFrame()
    expect(second.channel).not.toBe(first.channel)
  })

  it('PPTX viewer build is network isolated and embeds the local renderer protocol', async () => {
    const file = resolve(process.cwd(), 'generated/pptx-viewer.html')
    const html = await readFile(file, 'utf8')
    const artifact = new DOMParser().parseFromString(html, 'text/html')
    const policy = artifact.querySelector('meta[http-equiv="Content-Security-Policy"]')?.getAttribute('content') ?? ''
    const scripts = [...artifact.querySelectorAll('script')]

    expect(policy).toContain("connect-src 'none'")
    expect(policy).toContain("object-src 'none'")
    expect(policy).toContain("frame-src 'none'")
    expect(policy).toContain("worker-src 'none'")
    expect(scripts).toHaveLength(1)
    expect(scripts[0].hasAttribute('src')).toBe(false)
    expect(scripts[0].getAttribute('nonce')).toBe('YF_VIEWER_NONCE')
    expect(scripts[0].textContent).toContain('pptx:rendered')
    expect(scripts[0].textContent).toContain('pptx-preview-slide-wrapper')
    expect(artifact.querySelectorAll('link[href]')).toHaveLength(0)
  })
})
