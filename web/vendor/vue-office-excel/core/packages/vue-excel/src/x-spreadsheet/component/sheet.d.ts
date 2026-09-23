interface SheetSelection {
  range: { sri: number; sci: number; eri: number; eci: number }
  set(row: number, column: number, indexesUpdated?: boolean): void
  setEnd?(row: number, column: number, moving?: boolean): void
}

interface SheetForSelection {
  selector: SheetSelection
  data: { getCell(row: number, column: number): unknown }
  contextMenu: { setMode(mode: string): void }
  trigger(name: string, ...args: unknown[]): void
}

export function selectorSet(
  this: SheetForSelection,
  multiple: boolean,
  row: number,
  column: number,
  indexesUpdated?: boolean,
  moving?: boolean,
): void
export function sheetInitEvents(this: object): void

export default class Sheet {}
