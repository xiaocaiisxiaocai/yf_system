import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred } from './testUtils'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  createObjectURL: vi.fn(),
  revokeObjectURL: vi.fn(),
}))

vi.mock('../../../api/client', () => ({
  default: { get: mocks.get },
}))

import ImagePreview from '../../../components/ImagePreview'

let originalClientWidth: PropertyDescriptor | undefined
let originalClientHeight: PropertyDescriptor | undefined

beforeEach(() => {
  let objectUrl = 0
  mocks.createObjectURL.mockImplementation(() => `blob:fixture-${++objectUrl}`)
  Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: mocks.createObjectURL })
  Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: mocks.revokeObjectURL })
  originalClientWidth = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'clientWidth')
  originalClientHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'clientHeight')
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', {
    configurable: true,
    get() { return this.classList?.contains('image-preview-viewport') ? 1032 : 0 },
  })
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
    configurable: true,
    get() { return this.classList?.contains('image-preview-viewport') ? 632 : 0 },
  })
})

afterEach(() => {
  if (originalClientWidth) Object.defineProperty(HTMLElement.prototype, 'clientWidth', originalClientWidth)
  if (originalClientHeight) Object.defineProperty(HTMLElement.prototype, 'clientHeight', originalClientHeight)
})

async function loadImage(width = 2000, height = 1000) {
  const image = await screen.findByAltText(/\.(?:png|jpg)$/)
  Object.defineProperty(image, 'naturalWidth', { configurable: true, value: width })
  Object.defineProperty(image, 'naturalHeight', { configurable: true, value: height })
  fireEvent.load(image)
  return image
}

describe('image preview', () => {
  it('image preview fits large images and supports original size and reset', async () => {
    mocks.get.mockResolvedValue({ data: new Blob(['image']) })
    const user = userEvent.setup()
    const view = render(<ImagePreview fileId={1} name="test.png" />)
    const image = await loadImage()

    expect(image).toHaveStyle({ width: '1000px' })
    await user.click(screen.getByRole('button', { name: '原始大小' }))
    expect(image).toHaveStyle({ width: '2000px' })
    await user.click(screen.getByRole('button', { name: '重置图片缩放' }))
    expect(image).toHaveStyle({ width: '1000px' })
    expect(screen.queryByRole('link')).not.toBeInTheDocument()

    view.unmount()
    expect(mocks.revokeObjectURL).toHaveBeenCalledWith('blob:fixture-1')
  })

  it('failed image decoding can retry and releases the failed object URL', async () => {
    mocks.get.mockResolvedValue({ data: new Blob(['image']) })
    const user = userEvent.setup()
    render(<ImagePreview fileId={2} name="test.png" />)
    fireEvent.error(await screen.findByAltText('test.png'))

    expect(screen.getByRole('alert')).toHaveTextContent('图片预览失败')
    await user.click(screen.getByRole('button', { name: '重试图片预览' }))
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2))
    expect(mocks.revokeObjectURL).toHaveBeenCalledWith('blob:fixture-1')
    expect(await screen.findByAltText('test.png')).toHaveAttribute('src', 'blob:fixture-2')
  })

  it('closing image preview cancels authentication request and ignores late bytes', async () => {
    const request = deferred<{ data: Blob }>()
    let requestOptions: { signal: AbortSignal; responseType: string } | undefined
    let requestUrl = ''
    mocks.get.mockImplementation((url, options) => {
      requestUrl = url
      requestOptions = options
      return request.promise
    })
    const view = render(<ImagePreview fileId={3} name="test.jpg" />)

    expect(requestUrl).toBe('/files/3/content')
    expect(requestOptions?.responseType).toBe('blob')
    view.unmount()
    expect(requestOptions?.signal.aborted).toBe(true)
    request.resolve({ data: new Blob(['late image']) })
    await request.promise
    await Promise.resolve()
    expect(mocks.createObjectURL).not.toHaveBeenCalled()
  })

  it('message image preview requests the supplied authenticated content path', async () => {
    mocks.get.mockResolvedValue({ data: new Blob(['message image']) })
    const view = render(<ImagePreview contentUrl="/messages/18/images/7" name="message.png" />)

    await screen.findByAltText('message.png')
    expect(mocks.get).toHaveBeenCalledWith('/messages/18/images/7', expect.objectContaining({ responseType: 'blob' }))
    expect(mocks.createObjectURL).toHaveBeenCalledOnce()
    view.unmount()
    expect(mocks.revokeObjectURL).toHaveBeenCalledWith('blob:fixture-1')
  })

  it('reuses an existing thumbnail blob URL without downloading or owning it', async () => {
    const view = render(<ImagePreview sourceUrl="blob:thumbnail" name="message.png" />)

    expect(await screen.findByAltText('message.png')).toHaveAttribute('src', 'blob:thumbnail')
    expect(mocks.get).not.toHaveBeenCalled()
    expect(mocks.createObjectURL).not.toHaveBeenCalled()
    view.unmount()
    expect(mocks.revokeObjectURL).not.toHaveBeenCalledWith('blob:thumbnail')
  })
})
