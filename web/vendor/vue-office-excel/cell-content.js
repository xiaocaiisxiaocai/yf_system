// A read-only formula-bar-style surface. Text never becomes HTML or workbook edits.
export function createCellContentBar(before) {
  const bar = document.createElement('section')
  bar.className = 'excel-cell-content'
  bar.setAttribute('aria-label', '单元格内容')
  const address = document.createElement('span')
  address.className = 'excel-cell-address'
  address.textContent = '—'
  const text = document.createElement('textarea')
  text.className = 'excel-cell-text'
  text.readOnly = true
  text.rows = 1
  text.setAttribute('aria-label', '单元格完整内容')
  text.placeholder = '选择单元格查看完整内容'
  const toggle = document.createElement('button')
  toggle.type = 'button'
  toggle.className = 'excel-cell-toggle'
  let expanded = false
  const expand = value => {
    expanded = value
    bar.classList.toggle('is-expanded', value)
    toggle.textContent = value ? '收起' : '展开'
    toggle.setAttribute('aria-expanded', String(value))
  }
  toggle.addEventListener('click', () => expand(!expanded))
  // Keep arrow keys, selection and copying local to this read-only text surface.
  text.addEventListener('keydown', event => event.stopPropagation())
  text.addEventListener('copy', event => event.stopPropagation())
  bar.append(address, text, toggle)
  before.before(bar)
  expand(false)
  return {
    show(cellAddress, value) {
      address.textContent = cellAddress || '—'
      address.title = cellAddress || ''
      text.value = value == null ? '' : String(value)
      text.scrollTop = 0
      expand(false)
      // Grow only when the selected content does not fit the compact line.
      if (text.scrollHeight > text.clientHeight + 2) expand(true)
    },
    expand() { expand(true); text.focus() },
  }
}

export function selectedCellContent(viewer, rowIndex, columnIndex) {
  if (rowIndex < 0 || columnIndex < 0) return { address: '', text: '' }
  const sheet = viewer?.workbookDataSource?._worksheets?.[viewer.sheetIndex]
  if (!sheet) return { address: '', text: '' }
  const original = sheet.getCell(rowIndex + 1, columnIndex + 1)
  const master = original.master || original
  const rendered = viewer.xs.sheet.data.getCell(master.row - 1, master.col - 1)
  return { address: master.address, text: rendered?.text ?? master.text ?? '' }
}
