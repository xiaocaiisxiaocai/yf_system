import type { PaginationProps } from '@arco-design/web-react'

export const DEFAULT_PAGE_SIZE = 100
export const PAGE_SIZE_OPTIONS = [50, 100, 200, 500]

export const TABLE_PAGINATION: PaginationProps = {
  defaultPageSize: DEFAULT_PAGE_SIZE,
  sizeOptions: PAGE_SIZE_OPTIONS,
  sizeCanChange: true,
  pageSizeChangeResetCurrent: true,
  showTotal: true,
}
