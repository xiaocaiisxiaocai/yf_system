import type { Element } from './element.js'

export default class Editor {
  constructor(formulas: unknown[], viewFn: () => { width: number; height: number }, rowHeight: number)
  el: Element
  setOffset(offset: Record<string, number> | null, suggestPosition?: string): void
  setCell(cell: { text?: string } | null, validator: unknown): void
  clear(): void
}
