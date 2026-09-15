/** Normalize mouse/trackpad units; Shift and horizontal gestures remain panning. */
export function wheelZoomFactor(event) {
  if (event.shiftKey || !Number.isFinite(event.deltaY) || event.deltaY === 0
    || Math.abs(event.deltaX || 0) > Math.abs(event.deltaY)) return 1
  const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? 120 : 1
  const delta = Math.max(-120, Math.min(120, event.deltaY * unit))
  return Math.exp(-delta * 0.0018)
}
