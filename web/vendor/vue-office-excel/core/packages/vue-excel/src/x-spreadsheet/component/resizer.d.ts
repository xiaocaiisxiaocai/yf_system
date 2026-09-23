import type { Element } from './element.js'

export default class Resizer {
  constructor(vertical?: boolean, minDistance?: number)
  vertical: boolean
  minDistance: number
  cRect: { left: number; top: number; width: number; height: number } | null
  el: Element
  lineEl: Element
  unhideHoverEl: Element
  finishedFn: ((rect: object, distance: number) => void) | null
  hide(): void
  mousedownHandler(event: MouseEvent): void
}
