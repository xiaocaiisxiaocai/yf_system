import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

const css = readFileSync(join(__dirname, '..', 'index.css'), 'utf8')
const auditLogCss = readFileSync(join(__dirname, '..', 'components', 'AuditLogParts.css'), 'utf8')
const imagePreviewCss = readFileSync(join(__dirname, '..', 'components', 'ImagePreview.css'), 'utf8')

function mediaBlocks(condition: string): string[] {
  const escaped = condition.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const matcher = new RegExp(`@media\\s+${escaped}\\s*\\{`, 'g')
  const blocks: string[] = []
  for (const match of css.matchAll(matcher)) {
    const start = match.index + match[0].length
    let depth = 1
    let cursor = start
    while (cursor < css.length && depth > 0) {
      if (css[cursor] === '{') depth += 1
      else if (css[cursor] === '}') depth -= 1
      cursor += 1
    }
    blocks.push(css.slice(start, cursor - 1))
  }
  return blocks
}

describe('shared CSS regression guards (index.css)', () => {
  it('R-06: styles wrapped disabled buttons inside .action-slot via descendant selector', () => {
    expect(css).toContain('.action-slot .arco-btn')
    expect(css).not.toContain('.action-slot > .arco-btn')
  })

  it('R-05: uses real Arco disabled/error/warning/focus classes and preserves hover border', () => {
    expect(css).not.toContain('.arco-select-view-disabled')
    expect(css).not.toContain('.arco-input-number-disabled')
    expect(css).not.toContain('.arco-textarea-wrapper:not(.arco-textarea-disabled)')
    expect(css).toContain('.arco-select-disabled')
    expect(css).toContain('.arco-select-error')
    expect(css).toContain('.arco-select-warning')
    expect(css).toContain('.arco-select-focused')
    expect(css).toContain('.arco-picker-error')
    expect(css).toContain('.arco-picker-warning')
    expect(css).toContain('.arco-picker-focused')
    expect(css).toContain('border-color: var(--color-border-3);')
  })

  it('R-08: dark mobile nav drawer styles the real .drawer-close-button hover state', () => {
    expect(css).toMatch(/\.mobile-nav-drawer\s+\.drawer-close-button:hover\s*\{[^}]*background:\s*rgb\(255 255 255 \/ 12%\);[^}]*color:\s*#fff;/)
    expect(css).not.toContain('.arco-drawer-close-icon.arco-icon-hover')
  })

  it('R-09: isolates non-fixed table header items so resize handles do not cover fixed headers', () => {
    expect(css).toMatch(
      /\.arco-table-th:not\(\.arco-table-col-fixed-left\):not\(\.arco-table-col-fixed-right\)\s+\.arco-table-th-item\s*\{\s*isolation:\s*isolate;\s*\}/,
    )
  })

  it('R-13: provides a global prefers-reduced-motion media query', () => {
    const blocks = mediaBlocks('(prefers-reduced-motion: reduce)')
    expect(blocks.length).toBeGreaterThan(0)
    const block = blocks.join('\n')
    expect(block).toContain('animation-duration: 0.01ms !important;')
    expect(block).toContain('animation-iteration-count: 1 !important;')
    expect(block).toContain('transition-duration: 0.01ms !important;')
    expect(block).toContain('scroll-behavior: auto !important;')
  })

  it('R-01 & R-02: releases SysConfig & OemMailRules flex clipping after base rules without breaking desktop >= 1201px', () => {
    const systemBaseIndex = css.indexOf('.system-page.page-card--table { height: calc(100dvh - 88px); min-height: 0; overflow: hidden; }')
    const oemBaseIndex = css.indexOf('.oem-mail-rules-card.page-card--table { height: calc(100dvh - 88px); min-height: 0; overflow: hidden; }')
    expect(systemBaseIndex).toBeGreaterThan(-1)
    expect(oemBaseIndex).toBeGreaterThan(-1)

    const afterBase = css.slice(Math.max(systemBaseIndex, oemBaseIndex))
    expect(afterBase).toContain('@media (max-width: 1200px)')
    expect(afterBase).not.toContain('@media (max-width: 1100px)')
    expect(afterBase).toContain('@media (max-height: 480px)')
    expect(afterBase).toContain('@media (max-width: 720px)')
    expect(afterBase).toContain('min-height: calc(100dvh - 76px);')
    expect(afterBase).toContain('min-height: calc(100dvh - 88px);')
    expect(afterBase).toContain('.system-config-tabs { flex: 1 0 auto; height: auto; }')
    expect(afterBase).toContain('.system-config-tabs > .arco-tabs-content { flex: 1 0 auto; overflow: visible; }')
    expect(afterBase).toContain('.oem-mail-rules-card > .arco-card-body { flex: 1 0 auto; }')
    expect(afterBase).toContain('.system-mail-log-table { flex: 1 0 auto; min-height: 320px; overflow: visible; }')
    expect(css).not.toMatch(/(?<!\.system-mail-editor )\.system-smtp-grid\s*\{\s*grid-template-columns:\s*(?:1fr|repeat\(2,\s*minmax\(0,\s*1fr\)\));\s*\}/)
  })

  it('R-07: wraps long SystemConfigTable descriptions and editor hints inside table cells', () => {
    expect(css).toMatch(/\.system-config-editor\s*>\s*span\s*\{[^}]*white-space:\s*normal;[^}]*overflow-wrap:\s*anywhere;/)
    expect(css).toMatch(/\.system-config-description\s*\{[^}]*white-space:\s*normal;[^}]*overflow-wrap:\s*anywhere;/)
  })

  it('R-11: resets modal title icon sizing for all preview controls while keeping Select icons 12px', () => {
    expect(css).toMatch(/\.file-preview-controls\s+\.arco-icon\s*\{\s*font-size:\s*inherit;\s*margin:\s*0;\s*vertical-align:\s*middle;\s*\}/)
    expect(css).toMatch(/\.file-preview-controls\s+\.arco-select\s+\.arco-icon\s*\{\s*font-size:\s*12px;\s*\}/)
  })

  it('R-12: keeps message secondary actions visible when Popconfirm is open and styles column drag-over indicator', () => {
    const hoverBlocks = mediaBlocks('(hover: hover)').join('\n')
    expect(hoverBlocks).toContain('.msg-item .msg-secondary-action.arco-popconfirm-open')
    expect(css).toMatch(/\.table-column-picker-item\.is-drag-over\s*\{\s*box-shadow:\s*inset 0 -2px 0 rgb\(var\(--primary-6\)\);\s*\}/)
  })

  it('R-15: restores focus-visible outlines on Arco expand and alert-close buttons without rewriting global focus rule', () => {
    expect(css).toContain(':where(button, a, [tabindex]):focus-visible { outline: 2px solid rgb(var(--primary-6)); outline-offset: 3px; }')
    expect(css).toMatch(
      /\.arco-table-expand-icon-cell button:focus-visible,\s*\.arco-table-cell-expand-icon button:focus-visible,\s*\.arco-alert-close-btn:focus-visible\s*\{\s*outline:\s*2px solid rgb\(var\(--primary-6\)\);\s*outline-offset:\s*1px;\s*\}/,
    )
  })

  it('R-18: defines --app-mono token for code/JSON surfaces and applies tabular-nums to main-app counters/timestamps', () => {
    expect(css).toContain("--app-mono: ui-monospace, 'SFMono-Regular', Menlo, Consolas, monospace;")
    expect(css).toMatch(/\.system-config-param\s*>\s*code\s*\{[^}]*font-family:\s*var\(--app-mono\);/)
    expect(auditLogCss).toMatch(/\.audit-source-grid\s*>\s*code\s*\{[^}]*font-family:\s*var\(--app-mono\);/)
    expect(auditLogCss).toMatch(/\.audit-detail\s+\.audit-json\s*\{[^}]*font-family:\s*var\(--app-mono\);/)
    expect(auditLogCss).not.toContain('Consolas')

    const tabularMatch = css.match(/([^{}]+)\{\s*font-variant-numeric:\s*tabular-nums;\s*\}/)
    expect(tabularMatch).not.toBeNull()
    const selectors = tabularMatch![1].split(',').map((selector) => selector.trim())
    expect(selectors).toEqual(expect.arrayContaining([
      '.arco-pagination',
      '.arco-pagination-total-text',
      '.arco-badge-number',
      '.arco-progress-line-text',
      '.pdf-preview-toolbar > span',
      '.dashboard-message-extra',
      '.smtp-diagnosis-time',
      '.file-selected-summary',
      '.project-activity-summary-value',
      '.project-activity-time',
      '.arco-timeline-item-label',
    ]))
  })

  it('R-20: removes sticky z-index: 3 mail-log table header, ellipses dashboard message tags, keeps video loading dark, and guards image grab cursor', () => {
    expect(css).not.toMatch(/\.system-mail-log\s+\.arco-table-header\s*\{[^}]*z-index:\s*3/)
    expect(css).toMatch(
      /\.dashboard-message-title\s+\.arco-tag\s*>\s*\.arco-tag-content\s*\{[^}]*min-width:\s*0;[^}]*overflow:\s*hidden;[^}]*text-overflow:\s*ellipsis;[^}]*white-space:\s*nowrap;/,
    )
    expect(css).toMatch(/\.video-preview-status\s*\{[^}]*background:\s*var\(--color-bg-1\);/)
    expect(css).toMatch(/\.video-preview-status--loading\s*\{[^}]*background:\s*#111;[^}]*color:\s*#e5e6eb;/)
    expect(imagePreviewCss).toMatch(/\.image-preview-stage img\s*\{[^}]*cursor:\s*default;/)
    expect(imagePreviewCss).toMatch(/\.image-preview-stage\.is-pannable img\s*\{\s*cursor:\s*grab;\s*\}/)
    expect(imagePreviewCss).toMatch(/\.image-preview-stage\.is-pannable img:active\s*\{\s*cursor:\s*grabbing;\s*\}/)
  })
})
