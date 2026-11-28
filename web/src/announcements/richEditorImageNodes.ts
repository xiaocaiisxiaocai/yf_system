import { Node } from '@tiptap/core'
import { ReactNodeViewRenderer } from '@tiptap/react'
import { ImageBlock, PendingImage } from './RichEditorImages'

export const UploadedImage = Node.create({
  name: 'image', group: 'block', atom: true, draggable: true,
  addAttributes() { return { assetId: { default: null }, caption: { default: '' }, width: { default: 100 } } },
  // Clipboard images must go through the upload handler. Never load a pasted HTML image URL.
  parseHTML() { return [] },
  renderHTML({ node }) { return ['figure', {}, ['figcaption', {}, String(node.attrs.caption ?? '')]] },
  addNodeView() { return ReactNodeViewRenderer(ImageBlock) },
})

export const UploadPlaceholder = Node.create({
  name: 'pendingImage', group: 'block', atom: true, selectable: true,
  addAttributes() { return { key: { default: '' }, original: { default: null } } },
  parseHTML() { return [] },
  renderHTML() { return ['span', {}, '待上传图片'] },
  addNodeView() { return ReactNodeViewRenderer(PendingImage) },
})
