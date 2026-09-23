export class Element {
  constructor(tag: string | globalThis.Element, className?: string)
  el: HTMLElement
  on(eventNames: string, handler: (event: Event) => void): this
  offset(value?: Record<string, number>): this | { top: number; left: number; height: number; width: number }
  contains(element: Node | null): boolean
  css(name: string, value?: string): this | string
  show(): this
  hide(): this
}

export function h(tag: string, className?: string): Element
