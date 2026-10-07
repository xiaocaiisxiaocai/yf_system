import { deflateRawSync } from 'node:zlib'
import JSZip from 'jszip'
import { describe, expect, it } from 'vitest'

import {
  ArchiveLimitError,
  assertArchiveWithinLimits,
  presentationArchiveLimits,
  spreadsheetArchiveLimits,
} from '../../vendor/archive-limits.js'
import { readExcelData } from '../../vendor/vue-office-excel/core/packages/vue-excel/src/excel.js'

const text = new TextEncoder()

function bytesOf(parts: number[][]) {
  const size = parts.reduce((sum, part) => sum + part.length, 0)
  const out = new Uint8Array(size)
  let offset = 0
  for (const part of parts) {
    out.set(part, offset)
    offset += part.length
  }
  return out
}

function u16(value: number) {
  return [value & 255, (value >> 8) & 255]
}

function u32(value: number) {
  return [value & 255, (value >> 8) & 255, (value >> 16) & 255, (value >> 24) & 255]
}

function zip(entries: Array<{ name: string; method: number; data: Uint8Array; uncompressedSize: number }>) {
  const locals: Uint8Array[] = []
  const centrals: number[][] = []
  let offset = 0
  for (const entry of entries) {
    const name = Array.from(text.encode(entry.name))
    const local = bytesOf([
      u32(0x04034b50), u16(20), u16(0), u16(entry.method), u16(0), u16(0), u32(0),
      u32(entry.data.length), u32(entry.uncompressedSize), u16(name.length), u16(0), name, Array.from(entry.data),
    ])
    locals.push(local)
    centrals.push([
      ...u32(0x02014b50), ...u16(20), ...u16(20), ...u16(0), ...u16(entry.method), ...u16(0), ...u16(0), ...u32(0),
      ...u32(entry.data.length), ...u32(entry.uncompressedSize), ...u16(name.length), ...u16(0), ...u16(0),
      ...u16(0), ...u16(0), ...u32(0), ...u32(offset), ...name,
    ])
    offset += local.length
  }
  const central = bytesOf(centrals)
  const eocd = bytesOf([[
    ...u32(0x06054b50), ...u16(0), ...u16(0), ...u16(entries.length), ...u16(entries.length),
    ...u32(central.length), ...u32(offset), ...u16(0),
  ]])
  return bytesOf([...locals.map(item => Array.from(item)), Array.from(central), Array.from(eocd)])
}

describe('preview archive limits', () => {
  it('accepts a small stored workbook entry', async () => {
    const data = text.encode('ok')
    const archive = zip([{ name: 'xl/workbook.xml', method: 0, data, uncompressedSize: data.length }])
    await expect(assertArchiveWithinLimits(archive, spreadsheetArchiveLimits)).resolves.toBeUndefined()
  })

  it('stops a deflate stream that grows past the cap even when the directory claims a small size', async () => {
    const payload = deflateRawSync(Buffer.alloc(64 * 1024))
    const archive = zip([{
      name: 'xl/worksheets/sheet1.xml',
      method: 8,
      data: payload,
      uncompressedSize: 32,
    }])
    await expect(assertArchiveWithinLimits(archive, {
      maxEntries: 10,
      maxEntryUncompressed: 1024,
      maxTotalUncompressed: 2048,
    })).rejects.toBeInstanceOf(ArchiveLimitError)
  })

  it('rejects a workbook whose directory declares an expansion ExcelJS would otherwise inflate', async () => {
    const payload = deflateRawSync(Buffer.from('<worksheet/>'))
    const archive = zip([{
      name: 'xl/worksheets/sheet1.xml',
      method: 8,
      data: payload,
      uncompressedSize: spreadsheetArchiveLimits.maxEntryUncompressed + 1,
    }])
    await expect(readExcelData(archive, false)).rejects.toBeInstanceOf(ArchiveLimitError)
  })

  it('accepts a JSZip archive of the kind the PPTX preview builds', async () => {
    const archive = new JSZip()
    archive.file('[Content_Types].xml', '<Types/>')
    archive.file('ppt/presentation.xml', '<p:presentation/>')
    const bytes = await archive.generateAsync({ type: 'uint8array' })
    await expect(assertArchiveWithinLimits(bytes, presentationArchiveLimits)).resolves.toBeUndefined()
  })

  it('rejects input that is neither ZIP nor a legacy OLE2 workbook', async () => {
    const data = text.encode('not a workbook')
    await expect(assertArchiveWithinLimits(data, spreadsheetArchiveLimits)).rejects.toBeInstanceOf(ArchiveLimitError)
    const ole2 = new Uint8Array([0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1])
    await expect(assertArchiveWithinLimits(ole2, spreadsheetArchiveLimits)).resolves.toBeUndefined()
    await expect(assertArchiveWithinLimits(ole2, presentationArchiveLimits)).rejects.toBeInstanceOf(ArchiveLimitError)
  })

  it('rejects an archive whose local header disagrees with the directory', async () => {
    const payload = deflateRawSync(Buffer.from('<worksheet/>'))
    const archive = zip([{ name: 'xl/worksheets/sheet1.xml', method: 8, data: payload, uncompressedSize: 12 }])
    await expect(assertArchiveWithinLimits(archive, spreadsheetArchiveLimits)).resolves.toBeUndefined()
    const local = new DataView(archive.buffer, archive.byteOffset)
    local.setUint16(8, 0, true)
    await expect(assertArchiveWithinLimits(archive, spreadsheetArchiveLimits)).rejects.toBeInstanceOf(ArchiveLimitError)
  })

  it('rejects bytes between the directory and its end record', async () => {
    const data = text.encode('ok')
    const archive = zip([{ name: 'a.xml', method: 0, data, uncompressedSize: data.length }])
    const eocd = archive.length - 22
    const padded = bytesOf([Array.from(archive.subarray(0, eocd)), [0], Array.from(archive.subarray(eocd))])
    await expect(assertArchiveWithinLimits(padded, spreadsheetArchiveLimits)).rejects.toBeInstanceOf(ArchiveLimitError)
  })

  it('rejects a deflate stream that does not end inside its declared size', async () => {
    const payload = deflateRawSync(Buffer.from('<worksheet>' + 'x'.repeat(4000) + '</worksheet>'))
    const archive = zip([{ name: 'a.xml', method: 8, data: payload.subarray(0, payload.length - 2), uncompressedSize: 4023 }])
    await expect(assertArchiveWithinLimits(archive, spreadsheetArchiveLimits)).rejects.toBeInstanceOf(ArchiveLimitError)
  })

  it('rejects entries that leave the archive', async () => {
    const data = text.encode('x')
    const archive = zip([{ name: '../secret.txt', method: 0, data, uncompressedSize: data.length }])
    await expect(assertArchiveWithinLimits(archive, spreadsheetArchiveLimits)).rejects.toBeInstanceOf(ArchiveLimitError)
  })
})
