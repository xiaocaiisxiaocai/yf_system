export interface Supplier {
  id: number
  name: string
  remark?: string
  status: 'ACTIVE' | 'DISABLED'
  createdAt: string
}

export interface Account {
  id: number
  employeeNo: string
  realName: string
  email: string
  status: 'ACTIVE' | 'DISABLED'
  roleId?: number | null
  roleName?: string | null
  lastLoginAt?: string | null
  createdAt: string
}

export interface RoleOption {
  id: number
  name: string
}

export interface SupplierFormValues {
  name: string
  remark?: string
}
