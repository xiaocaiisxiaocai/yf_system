import type { Editor, JSONContent } from '@tiptap/core'
import { Fragment, type Node as PmNode, type ResolvedPos } from '@tiptap/pm/model'
import { Selection } from '@tiptap/pm/state'

// Pick an entire numbered step when the cursor sits inside a list. Else move the
// top-level block, including a whole table or an image and its caption.
function siblingsAt($pos: ResolvedPos) {
  let depth = $pos.depth
  while (depth > 0 && !['orderedList', 'bulletList'].includes($pos.node(depth).type.name)) depth--
  const parent = $pos.node(depth)
  return { parent, start: $pos.start(depth), index: Math.min($pos.index(depth), parent.childCount - 1) }
}

export function selectedBlock(editor: Editor) {
  return siblingsAt(editor.state.selection.$from)
}

function reorder(editor: Editor, source: ReturnType<typeof siblingsAt>, to: number) {
  const { parent, start, index } = source
  if (index < 0 || to < 0 || to >= parent.childCount || index === to) return false
  const blocks: PmNode[] = []
  parent.forEach(child => blocks.push(child))
  const [block] = blocks.splice(index, 1)
  blocks.splice(to, 0, block)
  const fragment = Fragment.fromArray(blocks)
  if (!parent.type.validContent(fragment)) return false
  const tr = editor.state.tr.replaceWith(start, start + parent.content.size, fragment)
  const newStart = start + blocks.slice(0, to).reduce((length, node) => length + node.nodeSize, 0)
  tr.setSelection(Selection.near(tr.doc.resolve(newStart), 1))
  editor.view.dispatch(tr.scrollIntoView())
  editor.view.focus()
  return true
}

export function moveSelectedBlock(editor: Editor, offset: -1 | 1) {
  const source = selectedBlock(editor)
  return reorder(editor, source, source.index + offset)
}

export function dropSelectedBlock(editor: Editor, from: number, target: EventTarget | null, y: number) {
  if (!(target instanceof HTMLElement)) return false
  const source = siblingsAt(editor.state.doc.resolve(from))
  let insertion: number | null = null
  // A cursor below an atomic image already points to its next sibling. Hit-test
  // the actual block so the moved paragraph is inserted on the intended side.
  source.parent.forEach((_node, offset, index) => {
    const element = editor.view.nodeDOM(source.start + offset)
    if (element instanceof HTMLElement && element.contains(target)) {
      const rect = element.getBoundingClientRect()
      insertion = index + (y > rect.top + rect.height / 2 ? 1 : 0)
    }
  })
  return insertion !== null && reorder(editor, source, insertion > source.index ? insertion - 1 : insertion)
}

export function editorDocument(json: string | null, legacy: string): JSONContent {
  if (json) return JSON.parse(json) as JSONContent
  return { type: 'doc', content: legacy.split(/\r\n?|\n/).map(line =>
    line ? { type: 'paragraph', content: [{ type: 'text', text: line }] } : { type: 'paragraph' }) }
}

// Local upload placeholders are never part of a saved document. A failed
// replacement keeps the previously published image available in the draft.
export function persistedDocument(node: JSONContent): JSONContent | null {
  if (node.type === 'pendingImage') {
    return node.attrs?.original ? { type: 'image', attrs: node.attrs.original } : null
  }
  const result = { ...node }
  if (node.content) {
    result.content = node.content.map(persistedDocument).filter((child): child is JSONContent => child !== null)
    if (!result.content.length && node.type === 'doc') result.content.push({ type: 'paragraph' })
  }
  if (node.marks) {
    result.marks = node.marks.map(mark => mark.type === 'link' ? { type: 'link', attrs: { href: mark.attrs?.href } } : mark)
  }
  return result
}
