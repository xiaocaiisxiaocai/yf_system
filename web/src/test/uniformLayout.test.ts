import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it } from 'vitest'

const SRC = join(__dirname, '..')

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) return name === 'test' || name === 'generated' ? [] : sourceFiles(path)
    return /\.tsx?$/.test(name) ? [path] : []
  })
}

const files = sourceFiles(SRC).map((path) => ({ path: relative(SRC, path).replaceAll('\\', '/'), text: readFileSync(path, 'utf8') }))
const count = (text: string, pattern: RegExp) => text.match(pattern)?.length ?? 0

describe('全站统一的表格与侧边栏', () => {
  it('每张表格都能拖动列宽：用 ResizableTable，或页面自己接上 useAdjustableColumns', () => {
    const offenders = files.filter(({ path, text }) => {
      if (path === 'components/ResizableTable.tsx') return false
      const bare = count(text, /<Table(?=[<\s])/g)
      return bare > count(text, /useAdjustableColumns\(/g)
    }).map(({ path }) => path)
    expect(offenders).toEqual([])
  })

  it('自己接 useAdjustableColumns 的表格同样有表头「显示列」和统一分页', () => {
    const offenders = files.filter(({ path, text }) => path !== 'components/ResizableTable.tsx' && (
      /<ColumnResizeProvider(?![^>]*\bpicker=)[^>]*>/.test(text)
      || (/useAdjustableColumns\(/.test(text) && /pagination=\{\{/.test(text) && !/pagination=\{\{ \.\.\.TABLE_PAGINATION/.test(text))
    )).map(({ path }) => path)
    expect(offenders).toEqual([])
  })

  it('状态措辞统一为「启用 / 停用」，界面文字不再出现「禁用」', () => {
    const offenders = files.flatMap(({ path, text }) => text.split('\n')
      .filter((line) => line.includes('禁用') && !/^\s*(\/\/|\/\*|\*)/.test(line))
      .map((line) => `${path}: ${line.trim()}`))
    expect(offenders).toEqual([])
  })

  it('二级侧边栏都用可折叠的 NavSider，不另写 Sider', () => {
    const offenders = files.filter(({ path, text }) => path !== 'layouts/NavSider.tsx' && /<Sider\b|Layout\.Sider/.test(text))
      .map(({ path }) => path)
    expect(offenders).toEqual([])
  })
})
