import { memo, useEffect, useMemo, useRef, useState } from 'react'

const DEFAULT_TILE_COUNT = 24
const MAX_TILE_COUNT = 240

function tileCountFor(width: number, height: number) {
  if (width <= 0 || height <= 0) return DEFAULT_TILE_COUNT
  const compact = width <= 720
  const colWidth = compact ? 220 : 300
  const rowHeight = compact ? 110 : 160
  const cols = Math.max(1, Math.ceil(width / colWidth))
  const rows = Math.max(1, Math.ceil(height / rowHeight)) + 1
  return Math.min(MAX_TILE_COUNT, Math.max(DEFAULT_TILE_COUNT, cols * rows))
}

function formatWatermarkTime(value: Date) {
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())} ${pad(value.getHours())}:${pad(value.getMinutes())}:${pad(value.getSeconds())}`
}

export const PreviewWatermark = memo(function PreviewWatermark({ employeeNo, realName }: {
  employeeNo?: string
  realName?: string
}) {
  const containerRef = useRef<HTMLDivElement>(null)
  const [openedAt] = useState(() => new Date())
  const [tileCount, setTileCount] = useState(DEFAULT_TILE_COUNT)

  useEffect(() => {
    const element = containerRef.current
    if (!element) return
    const update = () => {
      const next = tileCountFor(element.clientWidth, element.clientHeight)
      setTileCount((current) => (current === next ? current : next))
    }
    update()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(update)
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  const labels = useMemo(() => {
    const identity = [employeeNo?.trim(), realName?.trim()].filter((value): value is string => Boolean(value))
    const label = `${identity.length ? identity.join(' ') : '未知'} ${formatWatermarkTime(openedAt)}`
    return Array.from({ length: tileCount }, (_, tile) => <span key={`watermark-tile-${tile + 1}`}>{label}</span>)
  }, [employeeNo, realName, openedAt, tileCount])

  return <div ref={containerRef} className="preview-watermark" aria-hidden="true">{labels}</div>
})
