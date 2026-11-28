import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeAll, describe, expect, it, vi } from 'vitest'
import { Editor, Node, type JSONContent } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import RichAnnouncementEditor from '../announcements/RichAnnouncementEditor'
import { dropSelectedBlock } from '../announcements/richEditorBlocks'

beforeAll(() => {
  // ProseMirror uses these browser layout APIs to scroll its selection into view.
  if (!Range.prototype.getClientRects) Object.defineProperty(Range.prototype, 'getClientRects', { value: () => [] })
  if (!Range.prototype.getBoundingClientRect) Object.defineProperty(Range.prototype, 'getBoundingClientRect', { value: () => new DOMRect() })
  if (!URL.createObjectURL) URL.createObjectURL = () => 'blob:image-for-rich-editor'
  if (!URL.revokeObjectURL) URL.revokeObjectURL = () => {}
})

function deferred<T>() {
  let resolve!: (result: T) => void
  let reject!: (reason: Error) => void
  const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}

function setup(options: Partial<React.ComponentProps<typeof RichAnnouncementEditor>> = {}) {
  const change = vi.fn<(doc: string, plain: string) => void>()
  const unfinished = vi.fn()
  render(<RichAnnouncementEditor documentJson={null} legacyBody="第一步" onChange={change}
    loadImage={async () => new Blob(['preview'], { type: 'image/png' })} onUnfinishedChange={unfinished}
    uploadImage={async () => 1} {...options} />)
  const value = () => JSON.parse(change.mock.calls.at(-1)![0]) as JSONContent
  return { value, unfinished }
}

describe('公告图文编辑器', () => {
  it('把段落拖到图片下半部时移到图片正后方', () => {
    const editor = new Editor({
      extensions: [StarterKit, Node.create({ name: 'image', group: 'block', atom: true, renderHTML: () => ['figure'] })],
      content: { type: 'doc', content: [
        { type: 'paragraph', content: [{ type: 'text', text: '图后说明' }] },
        { type: 'image' },
        { type: 'paragraph', content: [{ type: 'text', text: '下一步' }] },
      ] },
    })
    try {
      const figure = editor.view.dom.querySelector('figure')!
      vi.spyOn(figure, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 100, 100))
      expect(dropSelectedBlock(editor, 1, figure, 75)).toBe(true)
      const doc: JSONContent = editor.getJSON()
      expect(doc.content?.slice(0, 3).map(n => n.content?.[0].text ?? n.type)).toEqual(['image', '图后说明', '下一步'])
    } finally { editor.destroy() }
  })

  it('顺序粘贴的两张图反序上传完成时仍保留原位；失败项复用原请求号重试', async () => {
    const first = deferred<number>(), second = deferred<number>()
    const upload = vi.fn<React.ComponentProps<typeof RichAnnouncementEditor>['uploadImage']>()
      .mockImplementationOnce(() => first.promise).mockImplementationOnce(() => second.promise).mockResolvedValue(41)
    const { value, unfinished } = setup({ uploadImage: upload })
    const files = ['before.png', 'after.png'].map(name => new File(['image'], name, { type: 'image/png' }))
    const textbox = screen.getByRole('textbox', { name: '公告正文' })
    fireEvent.paste(textbox, { clipboardData: { files, getData: () => '', types: ['Files'] } })
    expect(unfinished).toHaveBeenLastCalledWith(2)
    expect(upload.mock.calls.map(([file]) => file.name)).toEqual(['before.png', 'after.png'])
    await act(async () => { second.resolve(42); await second.promise })
    await act(async () => { first.reject(new Error('暂时离线')); await first.promise.catch(() => {}) })
    expect(unfinished).toHaveBeenLastCalledWith(1)
    expect(JSON.stringify(value())).not.toContain('pendingImage')
    fireEvent.click(screen.getByRole('button', { name: '重试图片' }))
    await waitFor(() => expect(unfinished).toHaveBeenLastCalledWith(0))
    expect(upload.mock.calls[0][3]).toBe(upload.mock.calls[2][3])
    expect(value().content?.filter(n => n.type === 'image').map(n => n.attrs?.assetId)).toEqual([41, 42])
  })

  it('替换图片时保留图注与宽度，删除后保存的文档不再引用该图', async () => {
    const original = { type: 'image', attrs: { assetId: 11, caption: '旧标签', width: 75 } }
    const replacement = deferred<number>()
    const { value } = setup({ documentJson: JSON.stringify({ type: 'doc', content: [original] }), uploadImage: () => replacement.promise })
    fireEvent.change(screen.getByLabelText('选择替换图片'), { target: { files: [new File(['new'], 'new.png', { type: 'image/png' })] } })
    expect(value().content?.[0]).toEqual(original)
    await act(async () => { replacement.resolve(12); await replacement.promise })
    const caption = screen.getByRole('textbox', { name: '图注' })
    fireEvent.change(caption, { target: { value: '新版标签' } })
    fireEvent.change(screen.getByRole('slider', { name: '图片宽度' }), { target: { value: '50' } })
    expect(value().content?.[0].attrs).toEqual({ assetId: 12, caption: '新版标签', width: 50 })
    fireEvent.click(screen.getByRole('button', { name: '移除图片' }))
    expect(value().content?.some(n => n.type === 'image')).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: '撤销' }))
    expect(value().content?.[0].attrs).toEqual({ assetId: 12, caption: '新版标签', width: 50 })
  })

  it('输入图注立即替换，撤销只恢复原图，重做恢复已上传的新图', async () => {
    const image = { type: 'image', attrs: { assetId: 21, caption: '', width: 75 } }
    const { value, unfinished } = setup({ documentJson: JSON.stringify({ type: 'doc', content: [image] }), uploadImage: async () => 22 })
    fireEvent.change(screen.getByRole('textbox', { name: '图注' }), { target: { value: '保留这段图注' } })
    fireEvent.change(screen.getByLabelText('选择替换图片'), { target: { files: [new File(['new'], 'label.png', { type: 'image/png' })] } })
    await waitFor(() => expect(value().content?.[0].attrs?.assetId).toBe(22))
    fireEvent.click(screen.getByRole('button', { name: '撤销' }))
    expect(value().content?.[0].attrs).toEqual({ assetId: 21, caption: '保留这段图注', width: 75 })
    fireEvent.click(screen.getByRole('button', { name: '重做' }))
    expect(value().content?.[0].attrs).toEqual({ assetId: 22, caption: '保留这段图注', width: 75 })
    expect(unfinished).toHaveBeenLastCalledWith(0)
  })

  it('编号步骤可在列表内独立移动并撤销', () => {
    const p = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
    const { value } = setup({ documentJson: JSON.stringify({ type: 'doc', content: [
      { type: 'orderedList', content: ['核对旧标签', '贴新标签'].map(text => ({ type: 'listItem', content: [p(text)] })) },
      p('末尾说明'),
    ] }) })
    fireEvent.click(screen.getByRole('button', { name: '下移' }))
    expect(value().content?.[0].content?.[0].content?.[0].content?.[0].text).toBe('贴新标签')
    fireEvent.click(screen.getByRole('button', { name: '撤销' }))
    expect(value().content?.[0].content?.[0].content?.[0].content?.[0].text).toBe('核对旧标签')
  })

  it('将旧纯文字的第一段移到第二段后，表格与链接可插入并保存为结构化正文', () => {
    const { value } = setup({ legacyBody: '包装前\n包装后' })
    fireEvent.click(screen.getByRole('button', { name: '下移' }))
    expect(value().content?.filter(node => node.type === 'paragraph' && node.content?.length)
      .map(node => node.content![0].text)).toEqual(['包装后', '包装前'])
    fireEvent.click(screen.getByRole('button', { name: '链接' }))
    fireEvent.change(screen.getByLabelText('链接地址'), { target: { value: 'javascript:invalid' } })
    fireEvent.click(screen.getByRole('button', { name: '应用链接' }))
    expect(screen.getByRole('alert')).toHaveTextContent('请输入完整的')
    fireEvent.change(screen.getByLabelText('链接地址'), { target: { value: 'https://example.com/guide' } })
    fireEvent.click(screen.getByRole('button', { name: '应用链接' }))
    expect(screen.getByRole('link', { name: 'https://example.com/guide' })).toHaveAttribute('href', 'https://example.com/guide')
    expect(JSON.stringify(value())).toContain('"attrs":{"href":"https://example.com/guide"}')
    fireEvent.click(screen.getByRole('button', { name: '表格' }))
    expect(value().content?.find(n => n.type === 'table')?.content).toHaveLength(2)
    fireEvent.click(screen.getByRole('button', { name: '增加行' }))
    expect(value().content?.find(n => n.type === 'table')?.content).toHaveLength(3)
  })
})
