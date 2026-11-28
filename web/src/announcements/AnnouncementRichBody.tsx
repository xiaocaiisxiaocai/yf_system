import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type { AxiosInstance } from 'axios'
import { Button, Message, Space } from '@arco-design/web-react'
import { fmtSize } from '../api/types'
import { requestErrorMessage } from '../api/requestError'
import { saveBlob } from '../utils/saveBlob'
import { FileBlobPreview } from '../oem/components/OemFilePreview'
import { oemPreviewKind } from '../oem/components/oemPreview'
import AnnouncementAssetImage from './AnnouncementAssetImage'
import { loadAnnouncementAsset, type AnnouncementAccess, type AnnouncementAsset } from './announcementAssetContent'
import './announcementRichBody.css'

interface DocumentNode {
  type: string
  text?: string
  attrs?: Record<string, unknown>
  marks?: { type: string; attrs?: Record<string, unknown> }[]
  content?: DocumentNode[]
}

function parseDocument(input?: string | null): DocumentNode | null {
  if (!input) return null
  try {
    const parsed: DocumentNode | null = JSON.parse(input)
    return parsed?.type === 'doc' && Array.isArray(parsed.content) ? parsed : null
  } catch {
    return null
  }
}

function markedText(node: DocumentNode): ReactNode {
  let text: ReactNode = typeof node.text === 'string' ? node.text : ''
  for (const mark of Array.isArray(node.marks) ? node.marks : []) {
    if (!mark) continue
    if (mark.type === 'bold') text = <strong>{text}</strong>
    const href = mark.attrs?.href
    if (mark.type === 'link' && typeof href === 'string' && /^(https?:\/\/|mailto:)/i.test(href)) {
      text = <a href={href} target="_blank" rel="noopener noreferrer">{text}</a>
    }
  }
  return text
}

function positiveInt(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0 ? value : undefined
}

function imageOrder(document: DocumentNode | null): number[] {
  const ids = new Set<number>()
  function visit(node: DocumentNode) {
    if (!node || typeof node !== 'object') return
    const id = positiveInt(node.attrs?.assetId)
    if (node.type === 'image' && id) ids.add(id)
    if (Array.isArray(node.content)) node.content.forEach(visit)
  }
  if (document) visit(document)
  return [...ids]
}

function renderNode(node: DocumentNode, renderImage: (attrs: Record<string, unknown>) => ReactNode): ReactNode {
  if (!node || typeof node !== 'object') return null
  if (node.type === 'text') return markedText(node)
  if (node.type === 'image') return renderImage(node.attrs ?? {})
  if (node.type === 'hardBreak') return <br />
  // Read-only document positions identify text and layout nodes; stateful images have their own asset/session key.
  const children = Array.isArray(node.content)
    // oxlint-disable-next-line react/no-array-index-key -- Text and layout nodes in a saved Tiptap document have no IDs.
    ? node.content.map((child, index) => <Fragment key={index}>{renderNode(child, renderImage)}</Fragment>) : undefined
  switch (node.type) {
    case 'doc': return children
    case 'paragraph': return <p>{children?.length ? children : <br />}</p>
    case 'heading': return node.attrs?.level === 3 ? <h3>{children}</h3> : <h2>{children}</h2>
    case 'bulletList': return <ul>{children}</ul>
    case 'orderedList': {
      const style = node.attrs?.type
      const type = style === 'a' || style === 'A' || style === 'i' || style === 'I' ? style : '1'
      return <ol start={positiveInt(node.attrs?.start)} type={type}>{children}</ol>
    }
    case 'listItem': return <li>{children}</li>
    case 'table': return <div className="announcement-table-scroll"><table><tbody>{children}</tbody></table></div>
    case 'tableRow': return <tr>{children}</tr>
    case 'tableCell':
    case 'tableHeader': {
      const Cell = node.type === 'tableHeader' ? 'th' : 'td'
      const align = node.attrs?.align
      const textAlign = align === 'left' || align === 'center' || align === 'right' ? align : undefined
      const widths = node.attrs?.colwidth
      const width = Array.isArray(widths) && widths.every(w => positiveInt(w) && w <= 4096)
        ? widths.reduce((sum: number, w: number) => sum + w, 0) : undefined
      return <Cell style={{ textAlign, width }}
        colSpan={positiveInt(node.attrs?.colspan)} rowSpan={positiveInt(node.attrs?.rowspan)}>{children}</Cell>
    }
    default: return null
  }
}

const extension = (name: string) => name.split('.').pop()?.toLowerCase() ?? ''

type FileContext = { announcementId: number; access: AnnouncementAccess; client?: AxiosInstance }

function AssetPreview({ asset, access, announcementId, client, onClose, navigation }: FileContext & {
  asset: AnnouncementAsset; onClose: () => void; navigation?: ReactNode
}) {
  const loadBlob = useCallback((id: number, signal: AbortSignal, onProgress?: (loaded: number) => void) =>
    loadAnnouncementAsset(announcementId, id, signal, { access, client, onProgress }), [announcementId, access, client])
  return <FileBlobPreview file={{ id: asset.id, originalName: asset.fileName, ext: extension(asset.fileName) }}
    realm={access === 'oem' ? 'oem' : 'internal'} loadBlob={loadBlob} navigation={navigation} onClose={onClose} />
}

export function AnnouncementAttachmentActions({ asset, access, announcementId, client }: FileContext & { asset: AnnouncementAsset }) {
  const [downloading, setDownloading] = useState(false)
  const [preview, setPreview] = useState(false)
  const request = useRef<AbortController | null>(null)
  useEffect(() => () => request.current?.abort(), [])
  async function download() {
    if (request.current) return
    const controller = new AbortController()
    request.current = controller
    setDownloading(true)
    try {
      const blob = await loadAnnouncementAsset(announcementId, asset.id, controller.signal, { access, client, download: true })
      if (!controller.signal.aborted) saveBlob(blob, asset.fileName)
    } catch (error) {
      if (!controller.signal.aborted) Message.error(requestErrorMessage(error, '附件下载失败'))
    } finally {
      request.current = null
      if (!controller.signal.aborted) setDownloading(false)
    }
  }
  return <>
    {oemPreviewKind(extension(asset.fileName)) &&
      <Button size="mini" type="text" aria-label={`预览 ${asset.fileName}`} onClick={() => setPreview(true)}>预览</Button>}
    <Button size="mini" type="text" aria-label={`下载 ${asset.fileName}`} loading={downloading} onClick={() => void download()}>下载</Button>
    {preview && <AssetPreview announcementId={announcementId} access={access} client={client}
      asset={asset} onClose={() => setPreview(false)} />}
  </>
}

export default function AnnouncementRichBody({ body, documentJson, assets = [], access, announcementId, client }: {
  body: string
  documentJson?: string | null
  assets?: readonly AnnouncementAsset[]
  access: AnnouncementAccess
  announcementId: number | null
  client?: AxiosInstance
}) {
  const document = useMemo(() => parseDocument(documentJson), [documentJson])
  const [previewId, setPreviewId] = useState<number | null>(null)
  const imageIds = useMemo(() => imageOrder(document), [document])
  const imageById = new Map(assets.filter(asset => asset.kind === 'IMAGE').map(asset => [asset.id, asset]))
  const images = imageIds.flatMap(id => {
    const asset = imageById.get(id)
    return asset ? [asset] : []
  })
  const selected = previewId === null ? null : imageById.get(previewId)
  const attachments = assets.filter(asset => asset.kind === 'ATTACHMENT')
  const index = images.findIndex(asset => asset.id === previewId)
  function renderImage(attrs: Record<string, unknown>) {
    const image = imageById.get(positiveInt(attrs.assetId) ?? 0)
    if (!image || announcementId === null) return null
    const caption = typeof attrs.caption === 'string' ? attrs.caption : ''
    const width = typeof attrs.width === 'number' ? Math.max(25, Math.min(100, attrs.width)) : 100
    return <figure style={{ width: `${width}%` }}>
      <AnnouncementAssetImage key={`${access}-${announcementId}-${image.id}`} assetId={image.id}
        announcementId={announcementId} access={access} client={client} alt={caption || image.fileName}
        onOpen={() => setPreviewId(image.id)} />
      {caption && <figcaption>{caption}</figcaption>}
    </figure>
  }
  return <section className="announcement-rich-body">
    {document ? renderNode(document, renderImage) : body && <p className="announcement-plain-body">{body}</p>}
    {announcementId !== null && attachments.length > 0 && <>
      <h3 className="announcement-attachment-heading">附件（{attachments.length}）</h3>
      <ul className="announcement-attachments">
        {attachments.map(asset => <li key={`${access}-${announcementId}-${asset.id}`} className="announcement-attachment">
          <span>{asset.fileName}</span><small>{fmtSize(asset.sizeBytes)}</small>
          <AnnouncementAttachmentActions asset={asset} announcementId={announcementId} access={access} client={client} />
        </li>)}
      </ul>
    </>}
    {selected && announcementId !== null && <AssetPreview asset={selected} announcementId={announcementId}
      access={access} client={client} onClose={() => setPreviewId(null)}
      navigation={index >= 0 && images.length > 1 && <Space>
        <Button size="mini" disabled={index === 0} onClick={() => setPreviewId(images[index - 1].id)}>上一张</Button>
        <span>{index + 1} / {images.length}</span>
        <Button size="mini" disabled={index === images.length - 1} onClick={() => setPreviewId(images[index + 1].id)}>下一张</Button>
      </Space>} />}
  </section>
}
