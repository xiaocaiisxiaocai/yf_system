import { useContext, useEffect, useRef, useState } from 'react'
import { NodeViewWrapper, type NodeViewProps } from '@tiptap/react'
import { closeHistory } from '@tiptap/pm/history'
import { ImageControlsContext, imageFormats } from './richEditorImageContext'

export function ImageBlock({ node, editor, getPos, updateAttributes, selected }: NodeViewProps) {
  const controls = useContext(ImageControlsContext)!
  const [result, setResult] = useState<{ id: number; attempt: number; url?: string; failed?: true } | null>(null)
  const [attempt, setAttempt] = useState(0)
  const fileInput = useRef<HTMLInputElement>(null)
  const id = node.attrs.assetId as number
  const load = controls.load
  useEffect(() => {
    const request = new AbortController()
    let source = ''
    void load(id, request.signal).then(blob => {
      if (request.signal.aborted) return
      source = URL.createObjectURL(blob)
      setResult({ id, attempt, url: source })
    }).catch(() => { if (!request.signal.aborted) setResult({ id, attempt, failed: true }) })
    return () => { request.abort(); if (source) URL.revokeObjectURL(source) }
  }, [id, attempt, load])
  const loaded = result?.id === id && result?.attempt === attempt ? result : null
  const selectImage = () => { const pos = getPos(); if (pos != null) editor.commands.setNodeSelection(pos) }
  const removeImage = () => {
    const pos = getPos()
    if (pos == null) return
    editor.view.dispatch(closeHistory(editor.state.tr.delete(pos, pos + node.nodeSize)))
    editor.view.dispatch(closeHistory(editor.state.tr))
  }
  return <NodeViewWrapper as="figure" className={'rich-image' + (selected ? ' rich-image--selected' : '')} contentEditable={false}>
    <div className="rich-image-frame" style={{ width: (node.attrs.width ?? 100) + '%' }}>
      {loaded?.url ? <img src={loaded.url} alt={node.attrs.caption || '公告插图'} draggable={false} onClick={selectImage} />
        : loaded?.failed ? <button type="button" onClick={() => setAttempt(n => n + 1)}>重新加载图片</button> : <span>正在加载图片…</span>}
    </div>
    <div className="rich-image-controls" onFocusCapture={selectImage}>
      <input aria-label="图注" placeholder="添加图注" value={node.attrs.caption ?? ''} maxLength={1000}
        disabled={controls.disabled} onChange={event => updateAttributes({ caption: event.target.value })} />
      <label>宽度 <input aria-label="图片宽度" type="range" min={25} max={100} step={1} value={node.attrs.width ?? 100}
        disabled={controls.disabled} onChange={event => updateAttributes({ width: Number(event.target.value) })} />
        {node.attrs.width ?? 100}%</label>
      <button type="button" disabled={controls.disabled} onClick={() => fileInput.current?.click()}>替换图片</button>
      <input ref={fileInput} type="file" accept={imageFormats} aria-label="选择替换图片" hidden onChange={event => {
        const file = event.target.files?.[0], pos = getPos()
        event.target.value = ''
        if (file && pos != null) controls.replace(file, pos)
      }} />
      <button type="button" disabled={controls.disabled} onClick={removeImage}>移除图片</button>
    </div>
  </NodeViewWrapper>
}

export function PendingImage({ node, getPos }: NodeViewProps) {
  const controls = useContext(ImageControlsContext)!
  const key = node.attrs.key as string
  const task = controls.tasks.get(key)
  return <NodeViewWrapper className="rich-pending-image" contentEditable={false}>
    <span>{task?.file.name || '待上传图片'}</span>
    <span role="status">{task ? task.error || `正在上传 ${task.percent}%` : '请移除后重新选择图片'}</span>
    {task?.error && <button type="button" disabled={controls.disabled} onClick={() => controls.retry(key)}>重试图片</button>}
    <button type="button" disabled={controls.disabled} onClick={() => { const pos = getPos(); if (pos != null) controls.remove(key, pos) }}>
      {node.attrs.original ? '取消替换' : '移除上传'}
    </button>
  </NodeViewWrapper>
}
