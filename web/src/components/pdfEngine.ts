import { getDocument, GlobalWorkerOptions, version } from 'pdfjs-dist'
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url'

GlobalWorkerOptions.workerSrc = workerUrl

export function openPdf(data: Uint8Array) {
  const assets = `${import.meta.env.BASE_URL}pdfjs/${version}/`
  return getDocument({ data, cMapUrl: `${assets}cmaps/`, cMapPacked: true,
    standardFontDataUrl: `${assets}standard_fonts/`, wasmUrl: `${assets}wasm/`, iccUrl: `${assets}iccs/` })
}
