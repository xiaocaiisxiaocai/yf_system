import { useCallback, useEffect, useState } from 'react'
import http from '../../../api/client'
import { type PageResp } from '../../../api/types'
import type { ApiResponses } from '../../../api/types'
import type { Supplier } from '../components/supplierTypes'

export function useSupplierListState() {
  const [data, setData] = useState<PageResp<Supplier>>({ list: [], total: 0, page: 1, pageSize: 10 })
  const [loading, setLoading] = useState(true)
  const [reloadKey, setReloadKey] = useState(0)
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState<string>()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [loadError, setLoadError] = useState(false)

  const fetchSuppliers = useCallback(async () => {
    const response = await http.get<ApiResponses['GET /admin/suppliers']>('/admin/suppliers', {
      params: { page, pageSize, keyword: keyword || undefined, status },
    })
    return response.data as PageResp<Supplier>
  }, [page, pageSize, keyword, status])

  const load = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((value) => value + 1)
  }, [])

  const search = useCallback((value: string) => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((current) => current + 1)
    setPage(1)
    setKeyword(value)
  }, [])

  const clearSearch = useCallback(() => {
    search('')
  }, [search])

  const changeStatus = useCallback((value: string | undefined) => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((current) => current + 1)
    setPage(1)
    setStatus(value)
  }, [])

  const changePage = useCallback((nextPage: number, nextPageSize: number) => {
    setLoading(true)
    setLoadError(false)
    setReloadKey((current) => current + 1)
    setPage(nextPage)
    setPageSize(nextPageSize)
  }, [])

  useEffect(() => {
    let active = true
    fetchSuppliers()
      .then((next) => {
        if (active) {
          setData(next)
          setLoadError(false)
          const lastPage = Math.max(1, Math.ceil(next.total / (next.pageSize || pageSize)))
          if (page > lastPage) setPage(lastPage)
        }
      })
      .catch(() => {
        if (active) setLoadError(true)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [fetchSuppliers, page, pageSize, reloadKey])

  return {
    data,
    loading,
    status,
    page,
    pageSize,
    loadError,
    load,
    search,
    clearSearch,
    changeStatus,
    changePage,
  }
}
