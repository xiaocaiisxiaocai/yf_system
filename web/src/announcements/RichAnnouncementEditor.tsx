import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { Editor } from '@tiptap/core'
import { closeHistory } from '@tiptap/pm/history'
import { TableKit } from '@tiptap/extension-table'
import { EditorContent, useEditor } from '@tiptap/react'
import StarterKit from '@tiptap/starter-kit'
import { newClientUuid } from '../utils/clientUuid'
import { requestErrorMessage } from '../api/requestError'
import { ImageControlsContext, imageFormats, type ImageTask } from './richEditorImageContext'
import { UploadedImage, UploadPlaceholder } from './richEditorImageNodes'
import { dropSelectedBlock, editorDocument, moveSelectedBlock, persistedDocument, selectedBlock } from './richEditorBlocks'
import './richAnnouncementEditor.css'

interface Props {
  documentJson: string | null
  legacyBody: string
  disabled?: boolean
  onChange: (documentJson: string, plainText: string) => void
  uploadImage: (file: File, progress: (percent: number) => void, signal: AbortSignal, uploadKey: string) => Promise<number>
  loadImage: (id: number, signal: AbortSignal) => Promise<Blob>
  onUnfinishedChange: (count: number) => void
}

function allowedLink(value: string) {
  try { return ['https:', 'http:', 'mailto:'].includes(new URL(value).protocol) } catch { return false }
}

const extensions = [
  StarterKit.configure({
    heading: { levels: [2, 3] },
    blockquote: false, code: false, codeBlock: false, horizontalRule: false, italic: false, strike: false, underline: false,
    link: { openOnClick: false, autolink: false, linkOnPaste: false, isAllowedUri: allowedLink },
  }),
  TableKit, UploadedImage, UploadPlaceholder,
]

function pendingImages(editor: Editor) {
  const found = new Map<string, number>()
  editor.state.doc.descendants((node, pos) => {
    if (node.type.name === 'pendingImage') found.set(node.attrs.key, pos)
  })
  return found
}

const dragType = 'application/x-yf-announcement-block'

export default function RichAnnouncementEditor(props: Props) {
  const latest = useRef(props)
  useLayoutEffect(() => { latest.current = props })
  const instance = useRef<Editor | null>(null)
  const taskMap = useRef(new Map<string, ImageTask>())
  const [tasks, setTasks] = useState<ReadonlyMap<string, ImageTask>>(new Map())
  const [link, setLink] = useState<string | null>(null)
  const [linkError, setLinkError] = useState('')
  const picker = useRef<HTMLInputElement>(null)
  const drag = useRef<{ from: number; doc: Editor['state']['doc'] } | null>(null)
  const load = useCallback((id: number, signal: AbortSignal) => latest.current.loadImage(id, signal), [])
  const [initialDoc] = useState(() => editorDocument(props.documentJson, props.legacyBody))

  const publishTasks = () => setTasks(new Map(taskMap.current))
  const runUpload = async (task: ImageTask) => {
    const signal = task.controller.signal
    try {
      const id = await latest.current.uploadImage(task.file, percent => {
        if (!signal.aborted) { task.percent = Math.round(percent); publishTasks() }
      }, signal, task.key)
      const current = instance.current
      if (!current || current.isDestroyed || signal.aborted) return
      const pos = pendingImages(current).get(task.key)
      if (pos == null) return
      const original = current.state.doc.nodeAt(pos)?.attrs.original
      const image = current.schema.nodes.image.create({ assetId: id, caption: original?.caption ?? '', width: original?.width ?? 100 })
      taskMap.current.delete(task.key)
      current.view.dispatch(current.state.tr.replaceWith(pos, pos + 1, image).setMeta('addToHistory', false))
      publishTasks()
    } catch (error) {
      if (!signal.aborted) { task.error = requestErrorMessage(error, '图片上传失败'); publishTasks() }
    }
  }

  function insertFiles(files: File[], at?: number, replacing = false) {
    const current = instance.current
    if (!current || !current.isEditable || !files.length) return
    const original = replacing && at != null ? current.state.doc.nodeAt(at)?.attrs : null
    const batch = files.map(file => ({ key: newClientUuid(), file, percent: 0, error: '', controller: new AbortController() }))
    const placeholders = batch.map(task => ({ type: 'pendingImage', attrs: { key: task.key, original } }))
    const range = at == null ? current.state.selection : { from: at, to: replacing ? at + 1 : at }
    for (const task of batch) taskMap.current.set(task.key, task)
    const inserted = current.chain().command(({ tr }) => { closeHistory(tr); return true })
      .insertContentAt({ from: range.from, to: range.to }, placeholders).run()
    if (!inserted) {
      for (const task of batch) taskMap.current.delete(task.key)
      return
    }
    current.view.dispatch(closeHistory(current.state.tr))
    publishTasks()
    for (const task of batch) void runUpload(task)
  }

  const editor = useEditor({
    extensions, content: initialDoc, editable: !props.disabled, shouldRerenderOnTransaction: true,
    editorProps: {
      attributes: { role: 'textbox', 'aria-label': '公告正文', 'aria-multiline': 'true', class: 'rich-announcement-document' },
      handlePaste: (_view, event) => {
        const images = Array.from(event.clipboardData?.files ?? []).filter(file => file.type.startsWith('image/'))
        if (!images.length) return false
        insertFiles(images)
        return true
      },
      handleDrop: (view, event) => {
        const transfer = event.dataTransfer
        const current = instance.current
        if (!transfer || !current || !current.isEditable) return false
        if (transfer.types.includes(dragType) && drag.current) {
          const source = drag.current
          drag.current = null
          if (source.doc !== current.state.doc) return true
          dropSelectedBlock(current, source.from, event.target, event.clientY)
          return true
        }
        const images = Array.from(transfer.files).filter(file => file.type.startsWith('image/'))
        if (!images.length) return false
        const target = view.posAtCoords({ left: event.clientX, top: event.clientY })
        if (!target) return false
        insertFiles(images, target.pos)
        return true
      },
      handleDOMEvents: { dragover: (_view, event) => {
        if (!drag.current || !event.dataTransfer?.types.includes(dragType)) return false
        event.preventDefault()
        return true
      } },
    },
    onUpdate: ({ editor: current }) => {
      const pending = pendingImages(current)
      // Undo or deleting a selected range can also remove a live upload.
      for (const task of taskMap.current.values()) {
        if (!pending.has(task.key) && !task.controller.signal.aborted) {
          task.controller.abort()
          task.error = '上传已取消'
        }
      }
      latest.current.onUnfinishedChange(pending.size)
      latest.current.onChange(JSON.stringify(persistedDocument(current.getJSON())), current.state.doc.textBetween(0, current.state.doc.content.size, '\n').trim())
    },
  })
  useLayoutEffect(() => { instance.current = editor }, [editor])

  useEffect(() => { editor?.setEditable(!props.disabled, false) }, [editor, props.disabled])
  useEffect(() => {
    const uploads = taskMap.current
    return () => { for (const task of uploads.values()) task.controller.abort() }
  }, [])

  if (!editor) return null
  function tool(label: string, action: () => void, active?: boolean, unavailable = false) {
    return <button type="button" aria-pressed={active} disabled={props.disabled || unavailable}
      onMouseDown={e => e.preventDefault()} onClick={action}>{label}</button>
  }
  const block = selectedBlock(editor)
  const insertLink = () => {
    const href = link?.trim() ?? ''
    if (!allowedLink(href)) { setLinkError('请输入完整的 http、https 或 mailto 链接'); return }
    if (editor.state.selection.empty && !editor.isActive('link')) {
      editor.chain().focus().insertContent({ type: 'text', text: href, marks: [{ type: 'link', attrs: { href } }] }).run()
    } else editor.chain().focus().extendMarkRange('link').setLink({ href }).run()
    setLink(null)
  }

  return <div className="rich-announcement-editor">
    <div role="toolbar" aria-label="正文排版" className="rich-announcement-toolbar">
      <select aria-label="段落样式" disabled={props.disabled}
        value={editor.isActive('heading', { level: 2 }) ? '2' : editor.isActive('heading', { level: 3 }) ? '3' : 'p'}
        onChange={e => e.target.value === 'p' ? editor.chain().focus().setParagraph().run()
          : editor.chain().focus().setHeading({ level: e.target.value === '2' ? 2 : 3 }).run()}>
        <option value="p">正文</option><option value="2">小标题</option><option value="3">次级标题</option>
      </select>
      {tool('加粗', () => editor.chain().focus().toggleBold().run(), editor.isActive('bold'))}
      {tool('编号列表', () => editor.chain().focus().toggleOrderedList().run(), editor.isActive('orderedList'))}
      {tool('项目符号', () => editor.chain().focus().toggleBulletList().run(), editor.isActive('bulletList'))}
      {tool('表格', () => editor.chain().focus().insertTable({ rows: 2, cols: 2, withHeaderRow: true }).run())}
      {tool('链接', () => { setLink(editor.getAttributes('link').href ?? ''); setLinkError('') }, editor.isActive('link'))}
      <button type="button" disabled={props.disabled} onMouseDown={event => event.preventDefault()}
        onClick={() => picker.current?.click()}>插入图片</button>
      {tool('撤销', () => editor.chain().focus().undo().run(), undefined, !editor.can().undo())}
      {tool('重做', () => editor.chain().focus().redo().run(), undefined, !editor.can().redo())}
      {tool('上移', () => moveSelectedBlock(editor, -1), undefined, block.index <= 0)}
      {tool('下移', () => moveSelectedBlock(editor, 1), undefined, block.index >= block.parent.childCount - 1)}
      <button type="button" draggable={!props.disabled} disabled={props.disabled || block.parent.childCount < 2}
        aria-label="拖动当前段落或步骤" title="选中段落、图片或编号步骤后，拖到目标位置"
        className="rich-block-grip"
        onDragEnd={() => { drag.current = null }}
        onDragStart={e => {
          drag.current = { from: editor.state.selection.from, doc: editor.state.doc }
          e.dataTransfer.setData(dragType, 'move')
          e.dataTransfer.effectAllowed = 'move'
        }}>⠿ 拖动</button>
    </div>
    {editor.isActive('table') && <div className="rich-announcement-toolbar" role="toolbar" aria-label="表格操作">
      {tool('增加行', () => editor.chain().focus().addRowAfter().run())}
      {tool('增加列', () => editor.chain().focus().addColumnAfter().run())}
      {tool('删除行', () => editor.chain().focus().deleteRow().run())}
      {tool('删除列', () => editor.chain().focus().deleteColumn().run())}
      {tool('移除表格', () => editor.chain().focus().deleteTable().run())}
    </div>}
    {link !== null && <div className="rich-announcement-toolbar">
      <input aria-label="链接地址" placeholder="https://" value={link} onChange={e => setLink(e.target.value)}
        onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); insertLink() } }} />
      {tool('应用链接', insertLink)}{tool('移除链接', () => { editor.chain().focus().extendMarkRange('link').unsetLink().run(); setLink(null) })}
      {tool('取消', () => setLink(null))}
      {linkError && <span role="alert">{linkError}</span>}
    </div>}
    <input hidden type="file" ref={picker} aria-label="选择正文图片" multiple accept={imageFormats} onChange={e => {
      insertFiles(Array.from(e.target.files ?? []))
      e.target.value = ''
    }} />
    <ImageControlsContext.Provider value={{
      disabled: !!props.disabled, tasks, load, replace: (file, pos) => insertFiles([file], pos, true),
      retry: key => {
        const task = taskMap.current.get(key)
        if (!task || !task.error) return
        task.error = ''; task.percent = 0; task.controller = new AbortController()
        publishTasks()
        void runUpload(task)
      },
      remove: (key, pos) => {
        const node = editor.state.doc.nodeAt(pos)
        if (!node || node.attrs.key !== key) return
        taskMap.current.get(key)?.controller.abort()
        const tr = editor.state.tr
        if (node.attrs.original) tr.replaceWith(pos, pos + 1, editor.schema.nodes.image.create(node.attrs.original))
        else tr.delete(pos, pos + 1)
        editor.view.dispatch(tr)
      },
    }}>
      <EditorContent editor={editor} />
    </ImageControlsContext.Provider>
  </div>
}
