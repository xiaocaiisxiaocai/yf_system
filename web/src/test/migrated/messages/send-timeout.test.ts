import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ post: vi.fn() }))

vi.mock('../../../api/client', () => ({ default: { post: mocks.post, get: vi.fn() } }))

import { MESSAGE_IMAGE_SEND_TIMEOUT_MS, startMessageSend } from '../../../components/message-panel/useMessageQueries'

describe('message send timeout', () => {
  beforeEach(() => {
    mocks.post.mockReset().mockResolvedValue({ data: {} })
  })

  it('image uploads get a long but bounded timeout and keep the same body (clientMessageId unchanged)', () => {
    const form = new FormData()
    form.append('clientMessageId', 'client-1')
    form.append('images', new File(['x'], 'a.png', { type: 'image/png' }))

    void startMessageSend(7, form)

    expect(MESSAGE_IMAGE_SEND_TIMEOUT_MS).toBeGreaterThan(60_000)
    expect(MESSAGE_IMAGE_SEND_TIMEOUT_MS).toBeLessThanOrEqual(10 * 60_000)
    expect(mocks.post).toHaveBeenCalledWith('/projects/7/messages', form, { timeout: MESSAGE_IMAGE_SEND_TIMEOUT_MS })
    expect((mocks.post.mock.calls[0][1] as FormData).get('clientMessageId')).toBe('client-1')
  })

  it('text-only messages keep the default client timeout', () => {
    const body = { content: 'hello', clientMessageId: 'client-2' }

    void startMessageSend(7, body)

    expect(mocks.post).toHaveBeenCalledWith('/projects/7/messages', body)
  })
})
