export interface MediaPosition {
  x: number
  y: number
  width: number
  height: number
}

export function calcPosition(
  sheet: object,
  range?: object,
  offset?: object | null,
  options?: { pixelRatio?: number; widthOffset?: number; heightOffset?: number },
): MediaPosition
export function toImageBytes(data: { buffer: Uint8Array | ArrayBuffer }): Uint8Array
export function renderImage(
  context: { drawImage(...args: unknown[]): void },
  medias: Array<{ buffer: Uint8Array | ArrayBuffer; extension: string }>,
  sheet: object,
  offset?: object,
  options?: object,
): void
export function clearCache(): void
