// Contracts of the OEM business line (/api/v1/oem). Kept separate from the
// collaboration types: the two lines share no DTOs.

export type Realm = 'internal' | 'oem'
export type Direction = 'INTERNAL_TO_OEM' | 'OEM_TO_INTERNAL'
export type Lifecycle = 'DRAFT' | 'SEALED' | 'RELEASED' | 'REJECTED' | 'BLOCKED' | 'CANCELLED' | 'ABANDONED'
export type ScanStatus = 'PENDING' | 'SCANNING' | 'CLEAN' | 'INFECTED' | 'ERROR' | 'UNSCANNABLE'
export type PayloadStatus = 'QUARANTINED' | 'PROMOTING' | 'AVAILABLE' | 'PURGE_PENDING' | 'PURGED' | 'STORAGE_LOST' | 'MISSING_UNVERIFIED'
export type ApprovalStatus = 'NOT_REQUIRED' | 'WAITING_SCAN' | 'PENDING' | 'APPROVAL_BLOCKED' | 'APPROVED' | 'SKIPPED' | 'REJECTED' | 'CANCELLED'
export type ContentPurpose = 'NONE' | 'RECIPIENT' | 'SENDER' | 'REVIEW'

export interface Page<T> {
  list: T[]
  total: number
  page: number
  pageSize: number
}

export interface PersonRef {
  realm: Realm | 'unknown'
  id: number
  employeeNo: string
  realName: string
}

export interface TransferSummary {
  id: number
  direction: Direction
  companyId: number
  companyName: string
  title: string
  sender: PersonRef
  lifecycleStatus: Lifecycle
  approvalStatus: ApprovalStatus | null
  approvalBlockedReason: string | null
  scanSummary: ScanStatus | null
  fileCount: number
  totalBytes: number
  availableCount: number
  purgePendingCount: number
  purgedCount: number
  missingCount: number
  createdAt: string
  sentAt: string | null
  releasedAt: string | null
  version: number
}

export interface TransferFile {
  id: number
  originalName: string
  ext: string
  sizeBytes: number
  sha256: string
  scanStatus: ScanStatus
  payloadStatus: PayloadStatus
  scanAttempts: number | null
  threatName: string | null
  scanMessage: string | null
  createdAt: string
  firstRecipientDownloadAt: string | null
  purgeDueAt: string | null
  purgedAt: string | null
  downloadable: boolean
}

export interface ApprovalTask {
  id: number
  status: string
  approverUserId: number
  approverName: string | null
  approverEmployeeNo: string | null
  reason: string | null
  decidedAt: string | null
  replacesTaskId: number | null
  reassignReason: string | null
  version: number
}

export interface ApprovalNode {
  sortNo: number
  name: string
  approverSource: string
  approvalMode: string
  status: string
  skipReason: string | null
  usedFallback: boolean
  completedAt: string | null
  tasks: ApprovalTask[]
}

export interface ApprovalInfo {
  instanceId: number
  status: string
  blockedReason: string | null
  currentSortNo: number | null
  version: number
  templateName: string | null
  nodes: ApprovalNode[]
}

export interface TransferDetail {
  summary: TransferSummary
  description: string | null
  retention: {
    templateId: number
    templateName: string | null
    mode: string | null
    releaseTtlMinutes: number | null
    receiptGraceMinutes: number | null
    summary: string | null
  }
  manifestSha256: string | null
  expiresAt: string | null
  closedReason: string | null
  closedAt: string | null
  capabilities: { canEdit: boolean; canSend: boolean; canDelete: boolean; canReadContent: boolean; contentPurpose: ContentPurpose }
  files: TransferFile[]
  approval: ApprovalInfo | null
}

export interface PendingTask {
  taskId: number
  version: number
  transferId: number
  title: string
  companyName: string
  senderName: string
  senderEmployeeNo: string
  nodeName: string
  approvalMode: string
  sentAt: string
  activatedAt: string
}

export interface Option {
  id: number
  name: string
}

export interface RetentionTemplate {
  id: number
  name: string
  mode: 'KEEP' | 'AFTER_RELEASE' | 'AFTER_FIRST_RECEIPT' | 'FIRST_RECEIPT_OR_DEADLINE'
  releaseTtlMinutes: number | null
  receiptGraceMinutes: number | null
  status: 'ACTIVE' | 'DISABLED'
  version: number
  summary: string
}

export interface Company {
  id: number
  name: string
  contactName: string | null
  contactPhone: string | null
  contactEmail: string | null
  remark: string | null
  status: 'ACTIVE' | 'DISABLED'
  accountCount?: number
  activeAccountCount?: number
  createdAt: string
}

export interface VendorAccount {
  id: number
  employeeNo: string
  realName: string
  email: string
  companyId: number
  status: 'ACTIVE' | 'DISABLED'
  mustChangePassword: boolean
  locked: boolean
  lastLoginAt: string | null
}

export interface TemplatePerson {
  id: number
  employeeNo: string | null
  realName: string | null
  eligible: boolean
}

export interface FlowNode {
  sortNo: number
  name: string
  approverSource: 'SECTION_LEADER' | 'DEPARTMENT_LEADER' | 'DIVISION_LEADER' | 'SPECIFIED_USERS'
  approvalMode: 'SINGLE' | 'ANY' | 'ALL'
  selfPolicy: 'DESIGNATED' | 'SKIP' | 'BLOCK'
  enabled: boolean
  approvers: TemplatePerson[]
  fallbacks: TemplatePerson[]
}

export interface FlowTemplate {
  id: number
  name: string
  isDefault: boolean
  status: 'ACTIVE' | 'DISABLED'
  version: number
  nodes: FlowNode[]
  scopes: { id: number; name: string; kind: string; status: string }[]
}

export interface FlowNodeInput {
  name: string
  approverSource: FlowNode['approverSource']
  approvalMode: FlowNode['approvalMode'] | null
  selfPolicy: FlowNode['selfPolicy']
  enabled: boolean
  approverUserIds: number[]
  fallbackUserIds: number[]
}

export interface RoutingPreview {
  ok: boolean
  reason?: string
  template?: Option
  matchedScope?: { id: number; name: string; kind: string } | null
  requiresApproval?: boolean
  nodes?: {
    sortNo: number
    name: string
    approverSource: string
    approvalMode: string
    skipped: boolean
    skipReason: string | null
    usedFallback: boolean
    scopeName: string | null
    approvers: ({ id: number; employeeNo: string; realName: string } | null)[]
  }[]
}

export interface SettingItem {
  key: string
  label: string
  kind: 'integer' | 'boolean' | 'extensions' | 'text'
  value: string
  min: number | null
  max: number | null
  readOnly?: boolean
  unsupportedReason?: string | null
}

export interface AuditRow {
  id: number
  action: string
  actorRealm: Realm | 'system'
  actorId: number | null
  employeeNo: string | null
  targetType: string | null
  targetId: string | null
  ip: string | null
  createdAt: string
  detail: Record<string, unknown> | null
}

export interface DepartmentNode {
  id: number
  name: string
  kind: 'DIVISION' | 'DEPARTMENT' | 'SECTION'
  status: string
  leader: { id: number; employeeNo: string; realName: string; active: boolean } | null
  children: DepartmentNode[]
}

export interface UploadInit {
  sessionId: string
  chunkSize: number
  totalChunks: number
  uploadedChunks: number[]
  resumed?: boolean
}
