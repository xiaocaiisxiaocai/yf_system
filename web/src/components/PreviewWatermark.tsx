import { memo, useMemo, useState } from 'react'

const WATERMARK_TILE_KEYS = Array.from({ length: 18 }, (_, tile) => `watermark-tile-${tile + 1}`)

function formatWatermarkTime(value: Date) {
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())} ${pad(value.getHours())}:${pad(value.getMinutes())}:${pad(value.getSeconds())}`
}

export const PreviewWatermark = memo(function PreviewWatermark({ employeeNo, realName }: {
  employeeNo?: string
  realName?: string
}) {
  const [openedAt] = useState(() => new Date())
  const labels = useMemo(() => {
    const label = `${employeeNo?.trim() || '未知'} ${realName?.trim() || '未知'} ${formatWatermarkTime(openedAt)}`
    return WATERMARK_TILE_KEYS.map(tile => <span key={tile}>{label}</span>)
  }, [employeeNo, realName, openedAt])
  return <div className="preview-watermark" aria-hidden="true">{labels}</div>
})
