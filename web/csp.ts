import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import type { Plugin } from 'vite'

// The Excel/PPTX viewers render in sandboxed srcdoc iframes, which inherit this page's policy.
// Their single inline script carries a nonce chosen at runtime, so the parent policy allows those
// exact scripts by content hash instead of allowing inline script in general.
export const VIEWER_FILES = ['generated/excel-viewer.html', 'generated/pptx-viewer.html']

const INLINE_SCRIPT = /<script\b(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script\s*>/gi

/** CSP hash sources for every inline script, hashed as the browser sees it (newlines normalized). */
export function inlineScriptHashes(html: string): string[] {
  return Array.from(html.matchAll(INLINE_SCRIPT), match => {
    const text = match[1].replace(/\r\n?/g, '\n')
    return `'sha256-${createHash('sha256').update(text, 'utf8').digest('base64')}'`
  })
}

export function contentSecurityPolicy(viewerHtml: string[]): string {
  const hashes = viewerHtml.flatMap(html => {
    const found = inlineScriptHashes(html)
    if (found.length !== 1) throw new Error(`preview viewer must contain exactly one inline script, found ${found.length}`)
    return found
  })
  return [
    "default-src 'self'",
    // PDF.js compiles its image decoders from WebAssembly.
    `script-src 'self' 'wasm-unsafe-eval' ${hashes.join(' ')}`,
    // Arco and the preview viewers set inline styles.
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self' data:",
    "media-src 'self' blob:",
    "connect-src 'self'",
    "worker-src 'self' blob:",
    "frame-src 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
  ].join('; ')
}

/** Adds the policy to the built index.html only; the dev server needs its own inline scripts. */
export function contentSecurityPolicyPlugin(root = process.cwd()): Plugin {
  return {
    name: 'yf-content-security-policy',
    apply: 'build',
    transformIndexHtml: {
      order: 'post',
      handler() {
        const viewers = VIEWER_FILES.map(file => readFileSync(resolve(root, file), 'utf8'))
        return [{
          tag: 'meta',
          attrs: { 'http-equiv': 'Content-Security-Policy', content: contentSecurityPolicy(viewers) },
          injectTo: 'head-prepend',
        }]
      },
    },
  }
}
