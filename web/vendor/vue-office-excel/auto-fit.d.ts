export interface AutoFitResult {
  widths: Map<number, number>
  heights: Map<number, number>
}

export interface AutoFitData {
  rows: {
    _: Record<number, { cells?: Record<number, { text?: unknown }> }>
    isHide(index: number): boolean
    getHeight(index: number): number
  }
  cols: {
    isHide(index: number): boolean
    getWidth(index: number): number
  }
  merges: {
    getFirstIncludes(row: number, column: number): {
      sri: number
      sci: number
      eri: number
      eci: number
    } | null
  }
  getCellStyleOrDefault(row: number, column: number): {
    textwrap?: boolean
    font?: { size?: number; name?: string; bold?: boolean; italic?: boolean }
  }
}

export function fitDimensions(
  data: AutoFitData,
  context: { font: string; measureText(text: string): { width: number } },
  axis?: 'all' | 'row' | 'col',
  start?: number,
  end?: number,
  pixelRatio?: number,
): AutoFitResult
