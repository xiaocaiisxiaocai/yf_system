import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({ post: vi.fn() }))

vi.mock('../../../api/client', () => ({
  default: { post: mocks.post },
}))

import VideoPreview from '../../../components/VideoPreview'

let pause: ReturnType<typeof vi.spyOn>
let load: ReturnType<typeof vi.spyOn>
let removeAttribute: ReturnType<typeof vi.spyOn>

beforeEach(() => {
  pause = vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => undefined)
  load = vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => undefined)
  removeAttribute = vi.spyOn(HTMLMediaElement.prototype, 'removeAttribute')
})

afterEach(() => {
  vi.useRealTimers()
})

describe('video preview', () => {
  it('video uses a same-origin media grant, renews it without replacing the player, and releases resources', async () => {
    vi.useFakeTimers()
    const requestOptions: Array<{ signal: AbortSignal }> = []
    mocks.post.mockImplementation(async (_url, _body, options) => {
      requestOptions.push(options)
      return { data: { url: '/api/v1/files/7/media', expiresInSeconds: 300 } }
    })
    const view = render(<VideoPreview fileId={7} />)
    await vi.waitFor(() => expect(mocks.post).toHaveBeenCalledOnce())
    const player = screen.getByLabelText('视频预览') as HTMLVideoElement
    await vi.waitFor(() => expect(player).toHaveAttribute('src', '/api/v1/files/7/media'))

    expect(player).toHaveAttribute('controlsList', 'nodownload noremoteplayback')
    expect(mocks.post).toHaveBeenCalledWith('/files/7/media-session', null, expect.objectContaining({ signal: expect.any(AbortSignal) }))
    fireEvent.loadedMetadata(player)
    expect(screen.queryByRole('status')).not.toBeInTheDocument()

    await vi.advanceTimersByTimeAsync(150_000)
    expect(mocks.post).toHaveBeenCalledTimes(2)
    expect(screen.getByLabelText('视频预览')).toBe(player)
    view.unmount()
    expect(requestOptions[0].signal.aborted).toBe(true)
    expect(pause).toHaveBeenCalled()
    expect(removeAttribute).toHaveBeenCalledWith('src')
    expect(load).toHaveBeenCalled()
  })

  it('video renewal failures retry with backoff without interrupting playback and fail only at expiry', async () => {
    vi.useFakeTimers()
    const grant = { data: { url: '/api/v1/files/7/media', expiresInSeconds: 300 } }
    mocks.post.mockResolvedValueOnce(grant).mockRejectedValue(new Error('offline'))
    render(<VideoPreview fileId={7} />)
    const player = screen.getByLabelText('视频预览') as HTMLVideoElement
    await vi.waitFor(() => expect(player).toHaveAttribute('src', '/api/v1/files/7/media'))
    fireEvent.loadedMetadata(player)
    pause.mockClear()

    // Renewal at half-life fails, then retries after 5s, 10s, 20s, 30s, 30s...
    await act(async () => { await vi.advanceTimersByTimeAsync(150_000) })
    expect(mocks.post).toHaveBeenCalledTimes(2)
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    expect(mocks.post).toHaveBeenCalledTimes(3)
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000) })
    expect(mocks.post).toHaveBeenCalledTimes(4)
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000) })
    expect(mocks.post).toHaveBeenCalledTimes(5)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(pause).not.toHaveBeenCalled()
    expect(player).toHaveAttribute('src', '/api/v1/files/7/media')

    // Keeps retrying until the grant is about to expire (300s minus the safety margin), then fails.
    await act(async () => { await vi.advanceTimersByTimeAsync(110_000) })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    expect(screen.getByRole('alert')).toHaveTextContent('视频无法播放')
    expect(pause).toHaveBeenCalled()
    const attempts = mocks.post.mock.calls.length
    await act(async () => { await vi.advanceTimersByTimeAsync(300_000) })
    expect(mocks.post).toHaveBeenCalledTimes(attempts)
  })

  it('video renewal recovers after a transient failure and resets its backoff', async () => {
    vi.useFakeTimers()
    const grant = { data: { url: '/api/v1/files/7/media', expiresInSeconds: 300 } }
    mocks.post.mockReset()
    mocks.post.mockResolvedValueOnce(grant).mockRejectedValueOnce(new Error('offline')).mockResolvedValue(grant)
    render(<VideoPreview fileId={7} />)
    const player = screen.getByLabelText('视频预览') as HTMLVideoElement
    await vi.waitFor(() => expect(player).toHaveAttribute('src', '/api/v1/files/7/media'))
    fireEvent.loadedMetadata(player)

    await act(async () => { await vi.advanceTimersByTimeAsync(155_000) })
    expect(mocks.post).toHaveBeenCalledTimes(3)
    // The recovered grant schedules the next renewal at its half-life again.
    await act(async () => { await vi.advanceTimersByTimeAsync(149_000) })
    expect(mocks.post).toHaveBeenCalledTimes(3)
    await act(async () => { await vi.advanceTimersByTimeAsync(1_000) })
    expect(mocks.post).toHaveBeenCalledTimes(4)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByLabelText('视频预览')).toBe(player)
  })

  it('video rejects foreign origins, query credentials, and mismatched file endpoints', async () => {
    for (const url of [
      'https://example.invalid/api/v1/files/7/media',
      '/api/v1/files/8/media',
      '/api/v1/files/7/media?token=secret',
    ]) {
      mocks.post.mockResolvedValueOnce({ data: { url, expiresInSeconds: 300 } })
      const view = render(<VideoPreview fileId={7} />)
      expect(await screen.findByRole('alert')).toHaveTextContent('视频无法播放')
      expect(screen.getByLabelText('视频预览')).not.toHaveAttribute('src')
      view.unmount()
    }
  })

  it('video retries failed grants and ignores late grants after close', async () => {
    vi.useFakeTimers()
    const lateGrant = deferred<{ data: { url: string; expiresInSeconds: number } }>()
    mocks.post.mockRejectedValueOnce(new Error('offline')).mockImplementationOnce(() => lateGrant.promise)
    const view = render(<VideoPreview fileId={7} />)

    await act(async () => { await Promise.resolve() })
    expect(screen.getByRole('alert')).toHaveTextContent('视频无法播放')
    const timerSpy = vi.spyOn(window, 'setTimeout')
    fireEvent.click(screen.getByRole('button', { name: '重试播放' }))
    expect(mocks.post).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('status')).toHaveTextContent('正在加载视频')
    view.unmount()
    await act(async () => {
      lateGrant.resolve({ data: { url: '/api/v1/files/7/media', expiresInSeconds: 300 } })
      await lateGrant.promise
      await Promise.resolve()
    })
    expect(screen.queryByLabelText('视频预览')).not.toBeInTheDocument()
    expect(timerSpy.mock.calls.filter(([, delay]) => delay === 150_000)).toHaveLength(0)

    const requestsAfterClose = mocks.post.mock.calls.length
    await vi.advanceTimersByTimeAsync(300_000)
    expect(mocks.post).toHaveBeenCalledTimes(requestsAfterClose)
  })
})
