export interface ResolvedColor {
  channels: number[]
  alpha: number
}

export function resolveColor(
  element: Element,
  theme?: Element | null,
  mapping?: Element | null,
  placeholder?: Element | null,
  depth?: number,
): ResolvedColor | null
export function relativePart(from: string, target: string): string | null
export function normalizePptx(zip: object): Promise<ArrayBuffer>
