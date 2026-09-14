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
      // Keep the grid stationary between the two clicks of a double-click.
      // Long content remains available through the explicit expand control.
      if (!cellAddress) expand(false)
    },
    expand() { expand(true); text.focus() },
  }
}

export function copyCellText(value) {
  const previous = document.activeElement
  const helper = document.createElement('textarea')
  helper.readOnly = true
  helper.value = value == null ? '' : String(value)
  Object.assign(helper.style, { position: 'fixed', left: '0', top: '0', width: '1px', height: '1px', opacity: '0' })
  document.body.append(helper)
  const onCopy = event => {
    if (!event.clipboardData) return
    event.clipboardData.setData('text/plain', helper.value)
    event.preventDefault()
    event.stopImmediatePropagation()
  }
  window.addEventListener('copy', onCopy, true)
  try {
    helper.focus({ preventScroll: true })
    helper.select()
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    window.removeEventListener('copy', onCopy, true)
    helper.remove()
    previous?.focus?.({ preventScroll: true })
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
