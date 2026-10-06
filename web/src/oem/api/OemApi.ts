import type { AxiosInstance, AxiosRequestConfig } from 'axios'
import type * as Api from '../../api/generated/api-types'
import type {
  AccountCreate, AccountUpdate, ActiveStatus, ApiResponses, ApiRoute, ApprovalStatus, ApproverOption, AuditRow, Company,
  CompanyListItem, CompanyOption, CompanyUpsert, DepartmentNode, DownloadSession, FlowNodeInput, FlowTemplate,
  FlowTemplateCreate, FlowTemplateDefinitionUpdate, FlowTemplateUpdate, InternalUserOption, Page, PendingTask,
  RetentionTemplate, RetentionTemplateCreate, RetentionTemplateUpdate, RoutingPreview, SettingItem, SettingUpdateItem,
  TransferCreate, TransferDetail, TransferSummary, TransferUpdate, UploadedFile, UploadInit, UploadInitRequest,
  VendorAccount,
} from './types'

export interface TransferQuery {
  page?: number
  pageSize?: number
  direction?: string
  status?: string
  approvalStatus?: ApprovalStatus
  keyword?: string
  mine?: boolean
}

type RouteOf<M extends string> = Extract<ApiRoute, `${M} ${string}`>

/**
 * The generated response with every member also allowed to be null. A UI type `T` bound to
 * route `K` must satisfy `T extends Relaxed<ApiResponses[K]>`: it may narrow string fields to
 * literal unions and restore nullability the OpenAPI document drops (nullable `$ref`s), but a
 * missing/renamed field or a changed primitive type fails to compile.
 */
type Relaxed<T> = T extends readonly (infer E)[]
  ? readonly (Relaxed<E> | null)[]
  : T extends object ? { [P in keyof T]: Relaxed<T[P]> | null } : T

/**
 * Typed gateway to /api/v1/oem. The same class serves internal staff (shared
 * collaboration HTTP client, internal session) and OEM accounts (portal HTTP
 * client, OEM session); the server decides what each realm may do.
 *
 * Each helper names the route key of the generated `ApiResponses` map and the UI type
 * returned for it; the UI type is checked against the generated response.
 */
export class OemApi {
  readonly http: AxiosInstance

  constructor(http: AxiosInstance) {
    this.http = http
  }

  private async get<K extends RouteOf<'GET'>, T extends Relaxed<ApiResponses[K]>>(url: string, params?: object): Promise<T> {
    return (await this.http.get<T>(url, { params })).data
  }

  private async post<K extends RouteOf<'POST'>, T extends Relaxed<ApiResponses[K]>>(url: string, body?: object, config?: AxiosRequestConfig): Promise<T> {
    const response = config ? await this.http.post<T>(url, body ?? {}, config) : await this.http.post<T>(url, body ?? {})
    return response.data
  }

  private async put<K extends RouteOf<'PUT'>, T extends Relaxed<ApiResponses[K]>>(url: string, body?: object): Promise<T> {
    return (await this.http.put<T>(url, body ?? {})).data
  }

  private async delete<K extends RouteOf<'DELETE'>>(url: string, params?: object): Promise<void> {
    if (params) await this.http.delete<ApiResponses[K]>(url, { params })
    else await this.http.delete<ApiResponses[K]>(url)
  }

  // Transfers
  listTransfers = (query: TransferQuery) =>
    this.get<'GET /oem/transfers', Page<TransferSummary>>('/oem/transfers', { ...query, mine: query.mine ? 'true' : undefined })
  transfer = (id: number) => this.get<'GET /oem/transfers/{id}', TransferDetail>(`/oem/transfers/${id}`)
  createTransfer = (body: TransferCreate) => this.post<'POST /oem/transfers', TransferDetail>('/oem/transfers', body)
  updateTransfer = (id: number, body: TransferUpdate) => this.put<'PUT /oem/transfers/{id}', TransferDetail>(`/oem/transfers/${id}`, body)
  deleteDraft = (id: number, version: number) => this.delete<'DELETE /oem/transfers/{id}'>(`/oem/transfers/${id}`, { version })
  send = (id: number, version: number) =>
    this.post<'POST /oem/transfers/{id}/send', TransferDetail>(`/oem/transfers/${id}/send`, { version } satisfies Api.TransferVersionRequest)
  cancel = (id: number, reason: string, version: number) =>
    this.post<'POST /oem/transfers/{id}/cancel', TransferDetail>(`/oem/transfers/${id}/cancel`, { reason, version } satisfies Api.CancelTransferRequest)
  removeFile = (fileId: number) => this.delete<'DELETE /oem/files/{id}'>(`/oem/files/${fileId}`)

  // Uploads
  initUpload = (transferId: number, body: UploadInitRequest) =>
    this.post<'POST /oem/transfers/{id}/uploads/init', UploadInit>(`/oem/transfers/${transferId}/uploads/init`, body)
  putChunk = async (sessionId: string, index: number, blob: Blob, signal?: AbortSignal) => {
    await this.http.put<ApiResponses['PUT /oem/uploads/{sessionId}/chunks/{index}']>(`/oem/uploads/${sessionId}/chunks/${index}`, blob, {
      headers: { 'Content-Type': 'application/octet-stream' }, signal, timeout: 0,
    })
  }
  merge = (sessionId: string) =>
    this.post<'POST /oem/uploads/{sessionId}/merge', UploadedFile>(`/oem/uploads/${sessionId}/merge`, {}, { timeout: 0 })
  abortUpload = (sessionId: string) => this.delete<'DELETE /oem/uploads/{sessionId}'>(`/oem/uploads/${sessionId}`)

  // Content
  previewBlob = async (fileId: number) =>
    (await this.http.get<ApiResponses['GET /oem/files/{id}/content']>(`/oem/files/${fileId}/content`, { responseType: 'blob', timeout: 0 })).data
  startDownload = (fileId: number) =>
    this.post<'POST /oem/files/{id}/download-sessions', DownloadSession>(`/oem/files/${fileId}/download-sessions`)

  // Approvals
  pendingApprovals = () => this.get<'GET /oem/approvals/pending', PendingTask[]>('/oem/approvals/pending')
  approve = (taskId: number, version: number, reason?: string) =>
    this.post<'POST /oem/approvals/{taskId}/approve', TransferDetail>(`/oem/approvals/${taskId}/approve`, { version, reason } satisfies Api.ApprovalDecisionRequest)
  reject = (taskId: number, version: number, reason: string) =>
    this.post<'POST /oem/approvals/{taskId}/reject', TransferDetail>(`/oem/approvals/${taskId}/reject`, { version, reason } satisfies Api.ApprovalDecisionRequest)
  reassign = (taskId: number, newApproverUserId: number, reason: string, version: number) =>
    this.post<'POST /oem/approvals/{taskId}/reassign', TransferDetail>(
      `/oem/approvals/${taskId}/reassign`, { newApproverUserId, reason, version } satisfies Api.ReassignTaskRequest)

  // Options for senders
  companyOptions = () => this.get<'GET /oem/company-options', CompanyOption[]>('/oem/company-options')
  retentionOptions = () => this.get<'GET /oem/retention-template-options', RetentionTemplate[]>('/oem/retention-template-options')

  // Administration (internal staff only)
  companies = (params: { page: number; pageSize: number; keyword?: string; status?: string }) =>
    this.get<'GET /oem/companies', Page<CompanyListItem>>('/oem/companies', params)
  createCompany = (body: CompanyUpsert) => this.post<'POST /oem/companies', Company>('/oem/companies', body)
  updateCompany = (id: number, body: CompanyUpsert) => this.put<'PUT /oem/companies/{id}', Company>(`/oem/companies/${id}`, body)
  setCompanyStatus = (id: number, status: ActiveStatus) =>
    this.put<'PUT /oem/companies/{id}/status', Company>(`/oem/companies/${id}/status`, { status } satisfies Api.OemStatusRequest)
  deleteCompany = (id: number) => this.delete<'DELETE /oem/companies/{id}'>(`/oem/companies/${id}`)
  accounts = (companyId: number) => this.get<'GET /oem/companies/{id}/accounts', VendorAccount[]>(`/oem/companies/${companyId}/accounts`)
  createAccount = (companyId: number, body: AccountCreate) =>
    this.post<'POST /oem/companies/{id}/accounts', VendorAccount>(`/oem/companies/${companyId}/accounts`, body)
  updateAccount = (id: number, body: AccountUpdate) => this.put<'PUT /oem/accounts/{id}', VendorAccount>(`/oem/accounts/${id}`, body)
  setAccountStatus = (id: number, status: ActiveStatus) =>
    this.put<'PUT /oem/accounts/{id}/status', VendorAccount>(`/oem/accounts/${id}/status`, { status } satisfies Api.OemStatusRequest)
  deleteAccount = (id: number) => this.delete<'DELETE /oem/accounts/{id}'>(`/oem/accounts/${id}`)
  resetAccountPassword = async (id: number, newPassword: string) => {
    await this.put<'PUT /oem/accounts/{id}/password', ApiResponses['PUT /oem/accounts/{id}/password']>(
      `/oem/accounts/${id}/password`, { newPassword } satisfies Api.OemPasswordReset)
  }

  retentionTemplates = () => this.get<'GET /oem/retention-templates', RetentionTemplate[]>('/oem/retention-templates')
  createRetention = (body: RetentionTemplateCreate) =>
    this.post<'POST /oem/retention-templates', RetentionTemplate>('/oem/retention-templates', body)
  updateRetention = (id: number, body: RetentionTemplateUpdate) =>
    this.put<'PUT /oem/retention-templates/{id}', RetentionTemplate>(`/oem/retention-templates/${id}`, body)

  flowTemplates = () => this.get<'GET /oem/flow-templates', FlowTemplate[]>('/oem/flow-templates')
  createFlowTemplate = (body: FlowTemplateCreate) => this.post<'POST /oem/flow-templates', FlowTemplate>('/oem/flow-templates', body)
  updateFlowTemplate = (id: number, body: FlowTemplateUpdate) =>
    this.put<'PUT /oem/flow-templates/{id}', FlowTemplate>(`/oem/flow-templates/${id}`, body)
  updateFlowTemplateDefinition = (id: number, body: FlowTemplateDefinitionUpdate) =>
    this.put<'PUT /oem/flow-templates/{id}/definition', FlowTemplate>(`/oem/flow-templates/${id}/definition`, body)
  replaceFlowNodes = (id: number, nodes: FlowNodeInput[], version: number) =>
    this.put<'PUT /oem/flow-templates/{id}/nodes', FlowTemplate>(`/oem/flow-templates/${id}/nodes`, { nodes, version })
  replaceFlowScopes = (id: number, departmentIds: number[], version: number) =>
    this.put<'PUT /oem/flow-templates/{id}/scopes', FlowTemplate>(
      `/oem/flow-templates/${id}/scopes`, { departmentIds, version } satisfies Api.FlowTemplateScopesUpdate)
  previewRouting = (userId: number) => this.get<'GET /oem/flow-templates/preview', RoutingPreview>('/oem/flow-templates/preview', { userId })
  approverOptions = (keyword?: string) => this.get<'GET /oem/approver-options', ApproverOption[]>('/oem/approver-options', { keyword })

  settings = (group: 'file' | 'notify') => group === 'file'
    ? this.get<'GET /oem/file-policies', SettingItem[]>('/oem/file-policies')
    : this.get<'GET /oem/notify-policies', SettingItem[]>('/oem/notify-policies')
  updateSettings = (group: 'file' | 'notify', items: SettingUpdateItem[]) => group === 'file'
    ? this.put<'PUT /oem/file-policies', SettingItem[]>('/oem/file-policies', { items } satisfies Api.OemSettingsUpdate)
    : this.put<'PUT /oem/notify-policies', SettingItem[]>('/oem/notify-policies', { items } satisfies Api.OemSettingsUpdate)

  auditLogs = (params: { page: number; pageSize: number; keyword?: string; action?: string }) =>
    this.get<'GET /oem/audit-logs', Page<AuditRow>>('/oem/audit-logs', params)

  // Shared organisation data used by approval-template scopes.
  departments = () => this.get<'GET /departments', DepartmentNode[]>('/departments')
  internalUsers = (keyword?: string) =>
    this.get<'GET /oem/internal-user-options', InternalUserOption[]>('/oem/internal-user-options', { keyword })
}
