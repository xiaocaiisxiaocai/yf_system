export interface SelectedCellContent {
  address: string
  text: unknown
}

export function createCellContentBar(before: Element): {
  show(cellAddress: string, value: unknown): void
  expand(): void
}
export function copyCellText(value: unknown): boolean
export function selectedCellContent(viewer: object, rowIndex: number, columnIndex: number): SelectedCellContent
