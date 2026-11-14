import type { PaginationProps } from '@arco-design/web-react'

/** Pagination every table shares: total count and a page-size switcher with the same choices. */
export const TABLE_PAGINATION: PaginationProps = { showTotal: true, sizeCanChange: true, sizeOptions: [10, 20, 50, 100] }
