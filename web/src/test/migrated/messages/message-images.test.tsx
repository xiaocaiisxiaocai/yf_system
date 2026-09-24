import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Message } from '@arco-design/web-react'
import { useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MessageImageComposer, pasteMessageImages } from '../../../components/MessageImages'

function sizedFile(name: string, type: string, size: number) {
  const file = new File(['fixture'], name, { type })
  Object.defineProperty(file, 'size', { configurable: true, value: size })
  return file
}

function PasteHarness({ initial = [] }: { initial?: File[] }) {
  const [files, setFiles] = useState(initial)
  return <div>
    <textarea
      aria-label="留言正文"
      onPaste={event => pasteMessageImages(event, files, setFiles)}
    />
    <output aria-label="已选图片">{files.map(file => file.name).join(',')}</output>
  </div>
}

function ComposerHarness({ initial }: { initial: File[] }) {
  const [files, setFiles] = useState(initial)
  return <MessageImageComposer files={files} onChange={setFiles} />
}

function paste(target: HTMLElement, files: File[]) {
  const preventDefault = Event.prototype.preventDefault
  const event = new Event('paste', { bubbles: true, cancelable: true }) as ClipboardEvent
  Object.defineProperty(event, 'clipboardData', {
    value: {
      items: files.map(file => ({ kind: 'file', type: file.type, getAsFile: () => file })),
      files,
    },
  })
  const spy = { prevented: false }
  event.preventDefault = () => {
    spy.prevented = true
    preventDefault.call(event)
  }
  fireEvent(target, event)
  return spy
}

describe('留言图片粘贴', () => {
  afterEach(() => Message.clear())

  it('message image paste leaves ordinary text paste untouched', () => {
    render(<PasteHarness />)
    const result = paste(screen.getByRole('textbox', { name: '留言正文' }), [])
    expect(result.prevented).toBe(false)
    expect(screen.getByRole('status', { name: '已选图片' })).toHaveTextContent('')
  })

  it('message image paste enforces the 50 MiB combined boundary', async () => {
    render(<PasteHarness initial={[sizedFile('first.png', 'image/png', 40 * 1024 * 1024)]} />)
    const result = paste(screen.getByRole('textbox', { name: '留言正文' }), [
      sizedFile('second.jpg', 'image/jpeg', 11 * 1024 * 1024),
    ])
    expect(result.prevented).toBe(true)
    expect(screen.getByRole('status', { name: '已选图片' })).toHaveTextContent('first.png')
    expect(await screen.findByText('本次图片总量最多 50 MiB')).toBeVisible()
  })

  it('message image paste accepts an exact 50 MiB combined payload', () => {
    render(<PasteHarness initial={[sizedFile('first.png', 'image/png', 40 * 1024 * 1024)]} />)
    const result = paste(screen.getByRole('textbox', { name: '留言正文' }), [
      sizedFile('second.webp', 'image/webp', 10 * 1024 * 1024),
    ])
    expect(result.prevented).toBe(true)
    expect(screen.getByRole('status', { name: '已选图片' })).toHaveTextContent('first.png,second.webp')
  })

  it('message image paste rejects files beyond nine images', async () => {
    const initial = Array.from({ length: 9 }, (_, index) => sizedFile(`${index}.png`, 'image/png', 1))
    render(<PasteHarness initial={initial} />)
    const result = paste(screen.getByRole('textbox', { name: '留言正文' }), [sizedFile('tenth.png', 'image/png', 1)])
    expect(result.prevented).toBe(true)
    expect(screen.getByRole('status', { name: '已选图片' })).not.toHaveTextContent('tenth.png')
    await waitFor(() => expect(screen.getByText('每条留言最多添加 9 张图片')).toBeVisible())
  })

  it('keeps the correct thumbnail blob when removing the first of identical file metadata', () => {
    const first = sizedFile('same.png', 'image/png', 1)
    const second = sizedFile('same.png', 'image/png', 1)
    const createObjectURL = vi.spyOn(URL, 'createObjectURL').mockImplementation(file =>
      file === first ? 'blob:first' : 'blob:second')
    const revokeObjectURL = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined)
    render(<ComposerHarness initial={[first, second]} />)

    expect(screen.getAllByAltText('same.png').map(image => image.getAttribute('src')))
      .toEqual(['blob:first', 'blob:second'])
    fireEvent.click(screen.getAllByRole('button', { name: '移除图片：same.png' })[0])

    expect(screen.getByAltText('same.png')).toHaveAttribute('src', 'blob:second')
    expect(createObjectURL).toHaveBeenCalledTimes(2)
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:first')
    expect(revokeObjectURL).not.toHaveBeenCalledWith('blob:second')
  })
})
