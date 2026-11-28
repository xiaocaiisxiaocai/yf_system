import type { DraftValues, ManageDetail } from './managementApi'

export type DraftPayload = DraftValues & { attachmentIds: number[] }

function draftFromDetail(source: ManageDetail): DraftPayload {
  return {
    title: source.title, body: source.body, documentJson: source.documentJson ?? null,
    internalScope: source.internalScope, externalScope: source.externalScope,
    departmentIds: source.departments.map(d => d.id), companyIds: source.companies.map(c => c.id),
    attachmentIds: source.assets.filter(asset => asset.kind === 'ATTACHMENT').map(asset => asset.id),
  }
}

// The server normalizes object property order and omits unset editor attributes.
// Preserve array order: the order of nodes, marks and attachments is part of the saved content.
function documentKey(json: string | null) {
  if (!json) return null
  return JSON.stringify(JSON.parse(json), (key, value: unknown) => {
    if (key === 'href' && typeof value === 'string') {
      try { return new URL(value).href } catch { return value }
    }
    if (!value || typeof value !== 'object' || Array.isArray(value)) return value
    return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== null).sort(([a], [b]) => a.localeCompare(b)))
  })
}

function contentKey(payload: DraftPayload) {
  return JSON.stringify([
    payload.title, payload.documentJson ? documentKey(payload.documentJson) : payload.body,
    payload.internalScope, [...payload.departmentIds].sort((a, b) => a - b),
    payload.externalScope, [...payload.companyIds].sort((a, b) => a - b), payload.attachmentIds,
  ])
}

export function matchesSavedDraft(detail: ManageDetail, sent: DraftPayload) {
  return !!detail.documentJson === !!sent.documentJson && contentKey(draftFromDetail(detail)) === contentKey(sent)
}

export function documentHasImage(documentJson: string | null): boolean {
  if (!documentJson) return false
  type Node = { type?: string; content?: Node[] }
  const visit = (node: Node): boolean => node.type === 'image' || (node.content?.some(visit) ?? false)
  return visit(JSON.parse(documentJson) as Node)
}
