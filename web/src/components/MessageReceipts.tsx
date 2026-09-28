import { useEffect } from 'react'
import { Button, Typography } from '@arco-design/web-react'
import { useQuery } from '@tanstack/react-query'
import { useShallow } from 'zustand/react/shallow'
import http, { type QuietRequestConfig } from '../api/client'
import { createSessionQueryScope, queryClient } from '../api/queryClient'
import type { ApiResponses } from '../api/types'
import { useAuth } from '../store/auth'

export interface Reader {
  userId: number
  realName: string
  userType: string
  readAt?: string | null
}

export interface ReadCounts {
  id: number
  readCount: number
  totalCount: number
}

const RECEIPT_SYNC_CONCURRENCY = 4

/** 分批读取留言回执人数；单条失败只跳过该条，不影响其它留言。 */
// eslint-disable-next-line react/only-export-components
export async function loadReadCounts(ids: number[], signal?: AbortSignal): Promise<ReadCounts[]> {
  const counts: ReadCounts[] = []
  for (let start = 0; start < ids.length; start += RECEIPT_SYNC_CONCURRENCY) {
    const batch = ids.slice(start, start + RECEIPT_SYNC_CONCURRENCY)
    const results = await Promise.all(batch.map(async (id) => {
      try {
        // 后台同步：失败只跳过该条，不能每条留言各弹一次网络错误提示。
        const config: QuietRequestConfig = { signal, quietNetworkError: true }
        const response = await http.get<ApiResponses['GET /messages/{id}/reads']>(`/messages/${id}/reads`, config)
        if (!Array.isArray(response.data?.readers) || !Array.isArray(response.data?.unread)) return null
        const readers = response.data.readers
        const unread = response.data.unread
        return { id, readCount: readers.length, totalCount: readers.length + unread.length }
      } catch {
        return null
      }
    }))
    counts.push(...results.filter((result): result is ReadCounts => result !== null))
  }
  return counts
}

/** 气泡内联的已读名单（轻量版） */
export function ReceiptBody({ id, refreshKey = 0, onLoaded }: {
  id: number
  refreshKey?: number
  onLoaded?: (id: number, readCount: number, totalCount: number) => void
}) {
  const scope = createSessionQueryScope(useAuth(useShallow((state) => ({
    user: state.user,
    generation: state.generation,
    permissions: state.permissions,
    menus: state.menus,
    mustChangePassword: state.mustChangePassword,
  }))))
  const query = useQuery<{ id: number; names: string[]; readCount: number; totalCount: number }>({
    queryKey: ['messages', 'receipt-body', scope, id, refreshKey],
    queryFn: async ({ signal }) => {
      // 失败在气泡内联显示“回执加载失败 / 重试”，不再额外弹出网络错误提示。
      const config: QuietRequestConfig = { signal, quietNetworkError: true }
      const response = await http.get<ApiResponses['GET /messages/{id}/reads']>(`/messages/${id}/reads`, config)
      return {
        id,
        names: response.data.readers.map((reader: Reader) => reader.realName),
        readCount: response.data.readers.length,
        totalCount: response.data.readers.length + response.data.unread.length,
      }
    },
    retry: false,
    staleTime: 0,
    gcTime: 0,
    refetchOnWindowFocus: false,
  }, queryClient)

  useEffect(() => {
    if (query.data) onLoaded?.(query.data.id, query.data.readCount, query.data.totalCount)
  }, [query.data, query.dataUpdatedAt, onLoaded])

  if (query.isPending) return <div style={{ fontSize: 12 }}>回执加载中…</div>
  if (query.isError) {
    return (
      <div style={{ fontSize: 12 }}>
        <Typography.Text type="error">回执加载失败</Typography.Text>
        <Button size="mini" type="text" onClick={() => void query.refetch()}>重试</Button>
      </div>
    )
  }
  return <div style={{ fontSize: 12 }}>{query.data.names.length ? query.data.names.join('、') : '暂无'}</div>
}
