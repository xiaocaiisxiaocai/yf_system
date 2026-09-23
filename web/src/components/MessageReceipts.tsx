import { useEffect, useRef, useState } from 'react'
import { Button, Typography } from '@arco-design/web-react'
import http from '../api/client'
import type { ApiResponses } from '../api/types'

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
export async function loadReadCounts(ids: number[]): Promise<ReadCounts[]> {
  const counts: ReadCounts[] = []
  for (let start = 0; start < ids.length; start += RECEIPT_SYNC_CONCURRENCY) {
    const batch = ids.slice(start, start + RECEIPT_SYNC_CONCURRENCY)
    const results = await Promise.all(batch.map(async (id) => {
      try {
        const response = await http.get<ApiResponses['GET /messages/{id}/reads']>(`/messages/${id}/reads`)
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
  const [receipt, setReceipt] = useState<{
    id: number
    names: string[]
    loading: boolean
    error: boolean
  }>(() => ({ id, names: [], loading: true, error: false }))
  const requestSeq = useRef(0)

  /** 首次加载与重试共用；较新的请求使旧响应失效。 */
  const fetchReceipt = (seq: number) => {
    http.get<ApiResponses['GET /messages/{id}/reads']>(`/messages/${id}/reads`)
      .then((r) => {
        if (seq !== requestSeq.current) return
        onLoaded?.(id, r.data.readers.length, r.data.readers.length + r.data.unread.length)
        setReceipt({
          id,
          names: r.data.readers.map((x: Reader) => x.realName),
          loading: false,
          error: false,
        })
      })
      .catch(() => {
        if (seq === requestSeq.current) {
          setReceipt({ id, names: [], loading: false, error: true })
        }
      })
  }

  const retry = () => {
    setReceipt({ id, names: [], loading: true, error: false })
    fetchReceipt(++requestSeq.current)
  }

  useEffect(() => {
    fetchReceipt(++requestSeq.current)
    return () => {
      requestSeq.current += 1
    }
    // fetchReceipt only closes over id and onLoaded, which are the effect's dependencies.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, refreshKey, onLoaded])

  const current = receipt.id === id
    ? receipt
    : { id, names: [], loading: true, error: false }

  if (current.loading) return <div style={{ fontSize: 12 }}>回执加载中…</div>
  if (current.error) {
    return (
      <div style={{ fontSize: 12 }}>
        <Typography.Text type="error">回执加载失败</Typography.Text>
        <Button size="mini" type="text" onClick={retry}>重试</Button>
      </div>
    )
  }
  return <div style={{ fontSize: 12 }}>{current.names.length ? current.names.join('、') : '暂无'}</div>
}
