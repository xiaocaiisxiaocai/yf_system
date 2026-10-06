// Contracts of the OEM business line (/api/v1/oem). Kept separate from the
// collaboration types: the two lines share no DTOs.
//
// Every DTO below is derived from the backend OpenAPI contract
// (../../api/generated/api-types.ts, `npm run generate:api-types`); this file only
// narrows enum-like string fields to the literal unions the UI relies on. A backend
// field rename/removal/type change therefore surfaces as a compile error here or at
// the use site instead of drifting silently.
import type * as Api from '../../api/generated/api-types'

export type { ApiResponses, ApiRoute } from '../../api/generated/api-types'

/**
 * Replaces fields of a generated DTO with narrower UI types. Every key of `N` must
 * exist on `T`, so a field renamed or removed by the backend fails to compile here.
 */
type Narrow<T, N extends { [K in keyof N]: K extends keyof T ? unknown : never }> = Omit<T, keyof N> & N

export type Realm = 'internal' | 'oem'
export type Direction = 'INTERNAL_TO_OEM' | 'OEM_TO_INTERNAL'
export type Lifecycle = 'DRAFT' | 'SEALED' | 'RELEASED' | 'REJECTED' | 'BLOCKED' | 'CANCELLED' | 'ABANDONED'
export type ValidationStatus = 'PENDING' | 'VALIDATING' | 'VALID' | 'INVALID' | 'ERROR'
export type PayloadStatus = 'QUARANTINED' | 'PROMOTING' | 'AVAILABLE' | 'PURGE_PENDING' | 'PURGED' | 'STORAGE_LOST' | 'MISSING_UNVERIFIED'
export type ApprovalStatus = 'NOT_REQUIRED' | 'WAITING_FILES' | 'PENDING' | 'APPROVAL_BLOCKED' | 'APPROVED' | 'SKIPPED' | 'REJECTED' | 'CANCELLED'
export type ContentPurpose = 'NONE' | 'RECIPIENT' | 'SENDER' | 'REVIEW'
export type ActiveStatus = 'ACTIVE' | 'DISABLED'
export type RetentionMode = 'KEEP' | 'AFTER_RELEASE' | 'AFTER_FIRST_RECEIPT' | 'FIRST_RECEIPT_OR_DEADLINE'
export type ApproverSource = 'SECTION_LEADER' | 'DEPARTMENT_LEADER' | 'DIVISION_LEADER' | 'SPECIFIED_USERS'
export type ApprovalMode = 'SINGLE' | 'ANY' | 'ALL'
export type SelfPolicy = 'DESIGNATED' | 'SKIP' | 'BLOCK'

/** All OEM paged lists share one envelope (OemPageResponse<T>). */
export type Page<T> = Omit<Api.OemPageResponseOfOemTransferSummaryResponse, 'list'> & { list: T[] }

export type PersonRef = Narrow<Api.OemPersonRefResponse, { realm: Realm | 'unknown' }>

export type TransferSummary = Narrow<Api.OemTransferSummaryResponse, {
  direction: Direction
  sender: PersonRef
  lifecycleStatus: Lifecycle
  approvalStatus: ApprovalStatus | null
  validationSummary: ValidationStatus | null
}>

export type TransferFile = Narrow<Api.OemTransferFileResponse, {
  validationStatus: ValidationStatus
  payloadStatus: PayloadStatus
}>

export type ApprovalTask = Api.OemApprovalTaskResponse
export type ApprovalNode = Api.OemApprovalNodeResponse
export type ApprovalInfo = Api.OemApprovalInfoResponse

export type TransferCapabilities = Narrow<Api.OemTransferCapabilitiesResponse, { contentPurpose: ContentPurpose }>

export type TransferDetail = Narrow<Api.OemTransferDetailResponse, {
  summary: TransferSummary
  capabilities: TransferCapabilities
  files: TransferFile[]
  approval: ApprovalInfo | null
}>

export type PendingTask = Api.OemPendingApprovalResponse

export type Option = Api.OemOptionResponse

export type CompanyOption = Api.OemCompanyOptionResponse

export type RetentionTemplate = Narrow<Api.OemRetentionTemplateResponse, { mode: RetentionMode; status: ActiveStatus }>

/** Single-company responses (create/update/status) carry no account counts; list rows do. */
export type Company = Narrow<Api.OemCompanyResponse, { status: ActiveStatus }>
  & Partial<Pick<Api.OemCompanyListItemResponse, 'accountCount' | 'activeAccountCount'>>

export type CompanyListItem = Narrow<Api.OemCompanyListItemResponse, { status: ActiveStatus }>

export type VendorAccount = Narrow<Api.OemAccountResponse, { status: ActiveStatus }>

export type TemplatePerson = Api.OemFlowNodePersonResponse

export type FlowNode = Narrow<Api.OemFlowNodeResponse, {
  approverSource: ApproverSource
  approvalMode: ApprovalMode
  selfPolicy: SelfPolicy
}>

export type FlowTemplate = Narrow<Api.OemFlowTemplateResponse, { status: ActiveStatus; nodes: FlowNode[] }>

export type FlowNodeInput = Narrow<Api.FlowNodeInput, {
  approverSource: ApproverSource
  approvalMode?: ApprovalMode | null
  selfPolicy: SelfPolicy
}>

export type RoutingNode = Api.OemRoutingNodeResponse

export type RoutingPreview = Narrow<Api.OemRoutingPreviewResponse, { nodes?: RoutingNode[] | null }>

export type SettingItem = Narrow<Api.OemSettingResponse, { kind: 'integer' | 'boolean' | 'extensions' | 'text' }>

export type AuditRow = Narrow<Api.OemAuditLogResponse, { actorRealm: Realm | 'system' }>

export type DepartmentNode = Narrow<Api.DepartmentTreeNode, {
  kind: 'DIVISION' | 'DEPARTMENT' | 'SECTION'
  children: DepartmentNode[]
}>

export type UploadInit = Api.OemUploadSessionInitResponse
export type UploadedFile = Api.OemUploadedFileResponse
export type DownloadSession = Api.OemDownloadSessionResponse
export type ApproverOption = Api.OemFlowPersonResponse
export type InternalUserOption = Api.OemInternalUserOptionResponse
export type AccountBrief = Api.OemAccountBrief

// Request bodies (generated request schemas, enum-like fields narrowed).
export type TransferCreate = Api.TransferCreate
export type TransferUpdate = Api.TransferUpdate
export type UploadInitRequest = Api.OemUploadInit
export type CompanyUpsert = Api.OemCompanyUpsert
export type AccountCreate = Api.OemAccountCreate
export type AccountUpdate = Api.OemAccountUpdate
export type RetentionTemplateCreate = Narrow<Api.RetentionTemplateCreate, { mode: RetentionMode }>
export type RetentionTemplateUpdate = Narrow<Api.RetentionTemplateUpdate, { mode: RetentionMode; status: ActiveStatus }>
export type FlowTemplateCreate = Narrow<Api.FlowTemplateCreate, { nodes: FlowNodeInput[] }>
export type FlowTemplateUpdate = Narrow<Api.FlowTemplateUpdate, { status: ActiveStatus }>
export type FlowTemplateDefinitionUpdate = Narrow<Api.FlowTemplateDefinitionUpdate, { status: ActiveStatus; nodes: FlowNodeInput[] }>
export type SettingUpdateItem = Api.OemSettingItem
