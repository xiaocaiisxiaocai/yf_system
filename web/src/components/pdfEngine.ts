// The legacy pair supplies the same API and worker with browser polyfills.
// Managed desktop browsers may not yet implement Uint8Array.toHex/toBase64.
import { getDocument, GlobalWorkerOptions, version } from 'pdfjs-dist/legacy/build/pdf.mjs'
import workerUrl from 'pdfjs-dist/legacy/build/pdf.worker.min.mjs?url'

GlobalWorkerOptions.workerSrc = workerUrl

export function openPdf(data: Uint8Array) {
  const assets = `${import.meta.env.BASE_URL}pdfjs/${version}/`
  return getDocument({ data, cMapUrl: `${assets}cmaps/`, cMapPacked: true,
    standardFontDataUrl: `${assets}standard_fonts/`, wasmUrl: `${assets}wasm/`, iccUrl: `${assets}iccs/` })
}
