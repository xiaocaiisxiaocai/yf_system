import { getFontSizePxByPt } from './core/packages/vue-excel/src/x-spreadsheet/core/font.js'

// Work only on populated cells, preserving empty/hidden geometry and merged ranges.
export function fitDimensions(data, context, axis = 'all', start = 0, end = Infinity, pixelRatio = 1) {
  const widths = new Map(), heights = new Map(), cells = []
  for (const [rowKey, row] of Object.entries(data.rows._)) {
    const ri = Number(rowKey)
    if (data.rows.isHide(ri) || data.rows.getHeight(ri) <= 0.1) continue
    for (const [colKey, cell] of Object.entries(row.cells || {})) {
      const ci = Number(colKey)
      if (data.cols.isHide(ci) || data.cols.getWidth(ci) <= 0.1 || cell.text == null || String(cell.text) === '') continue
      const merge = data.merges.getFirstIncludes(ri, ci)
      if (merge && (merge.sri !== ri || merge.sci !== ci)) continue
      cells.push({ ri, ci, text: String(cell.text), style: data.getCellStyleOrDefault(ri, ci), merge })
    }
  }
  const fontFor = style => {
    const font = style.font || {}
    const size = getFontSizePxByPt(font.size || 10)
    context.font = `${font.italic ? 'italic ' : ''}${font.bold ? 'bold ' : ''}${size}px ${font.name || 'Arial'}`
    return size
  }
  if (axis !== 'row') {
    for (const cell of cells) {
      if (cell.ci < start || cell.ci > end || (cell.merge && cell.merge.eci !== cell.ci)) continue
      fontFor(cell.style)
      let needed = 60
      for (const line of cell.text.split('\n')) needed = Math.max(needed, Math.ceil(context.measureText(line).width + 12))
      // Wrapped paragraphs keep a readable column; their full content determines row height.
      if (cell.style.textwrap) needed = Math.min(needed, Math.max(data.cols.getWidth(cell.ci), 360))
      widths.set(cell.ci, Math.max(widths.get(cell.ci) || 60, needed))
    }
  }
  if (axis !== 'col') {
    const merged = []
    for (const cell of cells) {
      const lastRow = cell.merge?.eri ?? cell.ri
      if (lastRow < start || cell.ri > end) continue
      const size = fontFor(cell.style)
      let width = 0
      for (let ci = cell.ci; ci <= (cell.merge?.eci ?? cell.ci); ci++) width += widths.get(ci) ?? data.cols.getWidth(ci)
      const innerWidth = Math.max(1, width - 12)
      let lines = 0
      for (const line of cell.text.split('\n')) {
        if (!cell.style.textwrap || context.measureText(line).width <= innerWidth) { lines++; continue }
        let used = 0
        lines++
        // Match the renderer's per-character wrap spacing in CSS pixels.
        for (let i = 0; i < line.length; i++) {
          if (used >= innerWidth) { lines++; used = 0 }
          used += context.measureText(line[i]).width + 1 / pixelRatio
        }
      }
      const needed = Math.max(24, Math.ceil(lines * (size + 2) + 12))
      if (lastRow === cell.ri) heights.set(cell.ri, Math.max(heights.get(cell.ri) || 24, needed))
      else merged.push({ first: cell.ri, last: lastRow, needed })
    }
    for (const { first, last, needed } of merged) {
      let total = 0, adjustable = -1
      for (let ri = first; ri <= last; ri++) {
        total += heights.get(ri) ?? data.rows.getHeight(ri)
        if (ri >= start && ri <= end && !data.rows.isHide(ri)) adjustable = ri
      }
      if (adjustable >= 0 && needed > total) heights.set(adjustable, (heights.get(adjustable) ?? data.rows.getHeight(adjustable)) + needed - total)
    }
  }
  return { widths, heights }
}
