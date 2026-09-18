import type { AxiosInstance } from 'axios'
import type {
  AuditRow, Company, DepartmentNode, FlowNodeInput, FlowTemplate, Option, Page, PendingTask, RetentionTemplate,
  RoutingPreview, SettingItem, TransferDetail, TransferSummary, UploadInit, VendorAccount,
} from './types'

export interface TransferQuery {
  page?: number
  pageSize?: number
  direction?: string
  status?: string
  keyword?: string
  mine?: boolean
}

/**
 * Typed gateway to /api/v1/oem. The same class serves internal staff (shared
 * collaboration HTTP client, internal session) and OEM accounts (portal HTTP
 * client, OEM session); the server decides what each realm may do.
 */
export class OemApi {
  readonly http: AxiosInstance

  constructor(http: AxiosInstance) {
    this.http = http
  }

  private async get<T>(url: string, params?: object): Promise<T> {
    return (await this.http.get<T>(url, { params })).data
  }

  private async post<T>(url: string, body?: object): Promise<T> {
    return (await this.http.post<T>(url, body ?? {})).data
  }

  private async put<T>(url: string, body?: object): Promise<T> {
    return (await this.http.put<T>(url, body ?? {})).data
  }

  // Transfers
  listTransfers = (query: TransferQuery) => this.get<Page<TransferSummary>>('/oem/transfers', { ...query, mine: query.mine ? 'true' : undefined })
  transfer = (id: number) => this.get<TransferDetail>(`/oem/transfers/${id}`)
  createTransfer = (body: { title: string; description?: string; oemCompanyId?: number; retentionTemplateId: number }) =>
    this.post<TransferDetail>('/oem/transfers', body)
  updateTransfer = (id: number, body: { title: string; description?: string; retentionTemplateId: number; version: number }) =>
    this.put<TransferDetail>(`/oem/transfers/${id}`, body)
  deleteDraft = async (id: number, version: number) => { await this.http.delete(`/oem/transfers/${id}`, { params: { version } }) }
  send = (id: number, version: number) => this.post<TransferDetail>(`/oem/transfers/${id}/send`, { version })
  cancel = (id: number, reason: string, version: number) => this.post<TransferDetail>(`/oem/transfers/${id}/cancel`, { reason, version })
  removeFile = async (fileId: number) => { await this.http.delete(`/oem/files/${fileId}`) }

  // Uploads
  initUpload = (transferId: number, body: { fileName: string; fileSize: number; fileMd5?: string }) =>
    this.post<UploadInit>(`/oem/transfers/${transferId}/uploads/init`, body)
  putChunk = async (sessionId: string, index: number, blob: Blob, signal?: AbortSignal) => {
    await this.http.put(`/oem/uploads/${sessionId}/chunks/${index}`, blob, {
      headers: { 'Content-Type': 'application/octet-stream' }, signal, timeout: 0,
    })
  }
  merge = (sessionId: string) => this.http.post(`/oem/uploads/${sessionId}/merge`, {}, { timeout: 0 }).then((r) => r.data)
  abortUpload = async (sessionId: string) => { await this.http.delete(`/oem/uploads/${sessionId}`) }

  // Content
  previewBlob = async (fileId: number) => (await this.http.get<Blob>(`/oem/files/${fileId}/content`, { responseType: 'blob', timeout: 0 })).data
  startDownload = (fileId: number) => this.post<{ downloadSessionId: string; url: string; expiresAt: string; purpose: string }>(`/oem/files/${fileId}/download-sessions`)

  // Approvals
  pendingApprovals = () => this.get<PendingTask[]>('/oem/approvals/pending')
  approve = (taskId: number, version: number, reason?: string) => this.post<TransferDetail>(`/oem/approvals/${taskId}/approve`, { version, reason })
  reject = (taskId: number, version: number, reason: string) => this.post<TransferDetail>(`/oem/approvals/${taskId}/reject`, { version, reason })
  reassign = (taskId: number, newApproverUserId: number, reason: string, version: number) =>
    this.post<TransferDetail>(`/oem/approvals/${taskId}/reassign`, { newApproverUserId, reason, version })

  // Options for senders
  companyOptions = () => this.get<Option[]>('/oem/company-options')
  retentionOptions = () => this.get<RetentionTemplate[]>('/oem/retention-template-options')

  // Administration (internal staff only)
  companies = (params: { page: number; pageSize: number; keyword?: string; status?: string }) => this.get<Page<Company>>('/oem/companies', params)
  createCompany = (body: Partial<Company>) => this.post<Company>('/oem/companies', body)
  updateCompany = (id: number, body: Partial<Company>) => this.put<Company>(`/oem/companies/${id}`, body)
  setCompanyStatus = (id: number, status: string) => this.put<Company>(`/oem/companies/${id}/status`, { status })
  accounts = (companyId: number) => this.get<VendorAccount[]>(`/oem/companies/${companyId}/accounts`)
  createAccount = (companyId: number, body: { employeeNo: string; realName: string; email: string; password: string }) =>
    this.post<VendorAccount>(`/oem/companies/${companyId}/accounts`, body)
  updateAccount = (id: number, body: { realName: string; email: string }) => this.put<VendorAccount>(`/oem/accounts/${id}`, body)
  setAccountStatus = (id: number, status: string) => this.put<VendorAccount>(`/oem/accounts/${id}/status`, { status })
  resetAccountPassword = async (id: number, newPassword: string) => { await this.put(`/oem/accounts/${id}/password`, { newPassword }) }

  retentionTemplates = () => this.get<RetentionTemplate[]>('/oem/retention-templates')
  createRetention = (body: Omit<RetentionTemplate, 'id' | 'status' | 'version' | 'summary'>) => this.post<RetentionTemplate>('/oem/retention-templates', body)
  updateRetention = (id: number, body: Omit<RetentionTemplate, 'id' | 'summary'>) => this.put<RetentionTemplate>(`/oem/retention-templates/${id}`, body)

  flowTemplates = () => this.get<FlowTemplate[]>('/oem/flow-templates')
  createFlowTemplate = (body: { name: string; isDefault: boolean; nodes: FlowNodeInput[]; departmentIds: number[] }) =>
    this.post<FlowTemplate>('/oem/flow-templates', body)
  updateFlowTemplate = (id: number, body: { name: string; status: string; isDefault: boolean; version: number }) =>
    this.put<FlowTemplate>(`/oem/flow-templates/${id}`, body)
  replaceFlowNodes = (id: number, nodes: FlowNodeInput[], version: number) => this.put<FlowTemplate>(`/oem/flow-templates/${id}/nodes`, { nodes, version })
  replaceFlowScopes = (id: number, departmentIds: number[], version: number) =>
    this.put<FlowTemplate>(`/oem/flow-templates/${id}/scopes`, { departmentIds, version })
  previewRouting = (userId: number) => this.get<RoutingPreview>('/oem/flow-templates/preview', { userId })
  approverOptions = (keyword?: string) => this.get<{ id: number; employeeNo: string; realName: string }[]>('/oem/approver-options', { keyword })

  settings = (group: 'file' | 'notify') => this.get<SettingItem[]>(group === 'file' ? '/oem/file-policies' : '/oem/notify-policies')
  updateSettings = (group: 'file' | 'notify', items: { key: string; value: string }[]) =>
    this.put<SettingItem[]>(group === 'file' ? '/oem/file-policies' : '/oem/notify-policies', { items })

  auditLogs = (params: { page: number; pageSize: number; keyword?: string; action?: string }) => this.get<Page<AuditRow>>('/oem/audit-logs', params)

  // Organisation leaders (shared organisation data, routed through the admin API)
  departments = () => this.get<DepartmentNode[]>('/departments')
  setLeader = (departmentId: number, leaderUserId: number | null) => this.put(`/admin/departments/${departmentId}/leader`, { leaderUserId })
  internalUsers = (keyword?: string) =>
    this.get<{ id: number; employeeNo: string; realName: string; canApprove: boolean; departmentName: string | null }[]>('/oem/internal-user-options', { keyword })
}
