import { Inflate } from 'pako/lib/inflate.js'

// Central-directory sizes are chosen by the file author. Parsers such as JSZip
// still inflate the whole compressed payload before comparing those sizes, so a
// small workbook can expand without a bound. Count inflated bytes in small
// chunks and stop at the preview cap. Discard each chunk; only the running
// total is kept.
//
// The count only bounds what the downstream parser does if both read the same
// structure. JSZip and SheetJS take the last end-of-directory signature, JSZip
// re-bases offsets when bytes precede the directory, and SheetJS trusts the
// local header (method, ZIP64 sizes) and inflates to the end of the stream
// rather than to the directory's compressed size. Any archive on which those
// readings could differ is refused instead of being measured one way.

export class ArchiveLimitError extends Error {
  constructor(message = '压缩包超出预览限制') {
    super(message)
    this.name = 'ArchiveLimitError'
  }
}

export const spreadsheetArchiveLimits = {
  maxEntries: 10000,
  maxEntryUncompressed: 64 * 1024 * 1024,
  maxTotalUncompressed: 256 * 1024 * 1024,
}

export const presentationArchiveLimits = {
  maxEntries: 5000,
  maxEntryUncompressed: 128 * 1024 * 1024,
  maxTotalUncompressed: 512 * 1024 * 1024,
  requireZip: true,
}

const eocdSignature = 0x06054b50
const centralSignature = 0x02014b50
const localSignature = 0x04034b50
const zip64ExtraId = 0x0001
const encryptedFlags = 0x2041
const dataDescriptorFlag = 0x0008

export async function assertArchiveWithinLimits(input, limits) {
  const bytes = toBytes(input)
  if (bytes.length < 4 || bytes[0] !== 0x50 || bytes[1] !== 0x4b) {
    // Only a legacy OLE2 workbook may skip the check; it is not a ZIP and its
    // SheetJS conversion is re-checked by the caller.
    if (!limits.requireZip && isOle2(bytes)) return
    throw new ArchiveLimitError()
  }
  const entries = readCentralDirectory(bytes, limits)
  let total = 0
  for (const entry of entries) {
    if (entry.directory) continue
    const size = await expandedSize(bytes, entry, limits.maxEntryUncompressed)
    if (size > limits.maxEntryUncompressed) throw new ArchiveLimitError()
    total += size
    if (total > limits.maxTotalUncompressed) throw new ArchiveLimitError()
  }
}

function toBytes(input) {
  if (input instanceof Uint8Array) return input
  if (input instanceof ArrayBuffer) return new Uint8Array(input)
  if (ArrayBuffer.isView(input)) {
    return new Uint8Array(input.buffer, input.byteOffset, input.byteLength)
  }
  throw new ArchiveLimitError()
}

function isOle2(bytes) {
  return bytes.length >= 8 && bytes[0] === 0xd0 && bytes[1] === 0xcf && bytes[2] === 0x11 && bytes[3] === 0xe0
}

function readCentralDirectory(bytes, limits) {
  const eocd = findEocd(bytes)
  const diskNumber = u16(bytes, eocd + 4)
  const centralDisk = u16(bytes, eocd + 6)
  const diskEntryCount = u16(bytes, eocd + 8)
  const entryCount = u16(bytes, eocd + 10)
  const centralSize = u32(bytes, eocd + 12)
  const centralOffset = u32(bytes, eocd + 16)
  if (entryCount === 0xffff || centralSize === 0xffffffff || centralOffset === 0xffffffff) {
    throw new ArchiveLimitError()
  }
  if (diskNumber !== 0 || centralDisk !== 0 || diskEntryCount !== entryCount) throw new ArchiveLimitError()
  if (entryCount > limits.maxEntries) throw new ArchiveLimitError()
  // The directory must end exactly where the end record starts. Otherwise
  // JSZip shifts every offset by the gap and reads a different directory.
  if (centralOffset > eocd || centralSize !== eocd - centralOffset) throw new ArchiveLimitError()
  const centralEnd = eocd
  const entries = []
  let offset = centralOffset
  while (offset + 46 <= centralEnd) {
    if (u32(bytes, offset) !== centralSignature) throw new ArchiveLimitError()
    if (entries.length >= limits.maxEntries) throw new ArchiveLimitError()
    const flags = u16(bytes, offset + 8)
    const method = u16(bytes, offset + 10)
    const compressedSize = u32(bytes, offset + 20)
    const uncompressedSize = u32(bytes, offset + 24)
    const nameLength = u16(bytes, offset + 28)
    const extraLength = u16(bytes, offset + 30)
    const commentLength = u16(bytes, offset + 32)
    const localOffset = u32(bytes, offset + 42)
    const nameStart = offset + 46
    const next = nameStart + nameLength + extraLength + commentLength
    if (next > centralEnd) throw new ArchiveLimitError()
    if (compressedSize === 0xffffffff || uncompressedSize === 0xffffffff || localOffset === 0xffffffff) {
      throw new ArchiveLimitError()
    }
    if (flags & encryptedFlags) throw new ArchiveLimitError()
    if (hasExtra(bytes, nameStart + nameLength, extraLength, zip64ExtraId)) throw new ArchiveLimitError()
    const name = bytes.subarray(nameStart, nameStart + nameLength)
    if (unsafeName(name)) throw new ArchiveLimitError()
    const directory = name.length > 0 && (name[name.length - 1] === 0x2f || name[name.length - 1] === 0x5c)
    if (!directory && method !== 0 && method !== 8) throw new ArchiveLimitError()
    if (!directory && uncompressedSize > limits.maxEntryUncompressed) throw new ArchiveLimitError()
    entries.push({ flags, method, compressedSize, uncompressedSize, localOffset, name, directory })
    offset = next
  }
  if (offset !== centralEnd || entries.length !== entryCount) throw new ArchiveLimitError()
  return entries
}

// JSZip and SheetJS both use the last end-record signature without checking
// that its comment reaches the end of the file. Use the same record and
// require it to be well formed, so no earlier record can stand in for it.
function findEocd(bytes) {
  const start = Math.max(0, bytes.length - 22 - 65535)
  for (let offset = bytes.length - 4; offset >= start; offset -= 1) {
    if (u32(bytes, offset) !== eocdSignature) continue
    if (offset + 22 > bytes.length) throw new ArchiveLimitError()
    const commentLength = u16(bytes, offset + 20)
    if (offset + 22 + commentLength !== bytes.length) throw new ArchiveLimitError()
    return offset
  }
  throw new ArchiveLimitError()
}

function hasExtra(bytes, start, length, id) {
  const end = start + length
  let offset = start
  while (offset + 4 <= end) {
    if (u16(bytes, offset) === id) return true
    offset += 4 + u16(bytes, offset + 2)
  }
  return false
}

function unsafeName(name) {
  if (name.length === 0 || name[0] === 0x2f || name[0] === 0x5c) return true
  let segmentStart = 0
  for (let index = 0; index <= name.length; index += 1) {
    const boundary = index === name.length || name[index] === 0x2f || name[index] === 0x5c
    if (!boundary) {
      if (name[index] === 0) return true
      continue
    }
    const length = index - segmentStart
    if (length === 2 && name[segmentStart] === 0x2e && name[segmentStart + 1] === 0x2e) return true
    if (length >= 2 && name[segmentStart + 1] === 0x3a) return true
    segmentStart = index + 1
  }
  return false
}

async function expandedSize(bytes, entry, maxOut) {
  if (entry.localOffset > bytes.length - 30) throw new ArchiveLimitError()
  const local = entry.localOffset
  if (u32(bytes, local) !== localSignature) throw new ArchiveLimitError()
  const flags = u16(bytes, local + 6)
  const method = u16(bytes, local + 8)
  const compressedSize = u32(bytes, local + 18)
  const uncompressedSize = u32(bytes, local + 22)
  const nameLength = u16(bytes, local + 26)
  const extraLength = u16(bytes, local + 28)
  const dataOffset = local + 30 + nameLength + extraLength
  if (dataOffset > bytes.length || entry.compressedSize > bytes.length - dataOffset) throw new ArchiveLimitError()
  // SheetJS reads the local header; JSZip reads the directory. They must agree.
  if (flags & encryptedFlags || method !== entry.method) throw new ArchiveLimitError()
  if (!sameBytes(bytes.subarray(local + 30, local + 30 + nameLength), entry.name)) throw new ArchiveLimitError()
  if (hasExtra(bytes, local + 30 + nameLength, extraLength, zip64ExtraId)) throw new ArchiveLimitError()
  if (!(flags & dataDescriptorFlag)
    && (compressedSize !== entry.compressedSize || uncompressedSize !== entry.uncompressedSize)) {
    throw new ArchiveLimitError()
  }
  if (entry.compressedSize === 0 && entry.uncompressedSize === 0) return 0
  if (entry.method === 0) {
    if (entry.compressedSize > maxOut) throw new ArchiveLimitError()
    return entry.compressedSize
  }
  return inflateAtMost(bytes.subarray(dataOffset, dataOffset + entry.compressedSize), maxOut)
}

// The deflate stream must end inside the declared compressed size and use all
// of it. A stream that runs past it would be inflated further by SheetJS,
// which decodes to the end of the stream instead of stopping at that size.
function inflateAtMost(slice, maxOut) {
  if (slice.length === 0) throw new ArchiveLimitError()
  let total = 0
  const inflator = new Inflate({ raw: true, chunkSize: 16 * 1024 })
  inflator.onData = (chunk) => {
    total += chunk.length
    if (total > maxOut) throw new ArchiveLimitError()
  }
  try {
    inflator.push(slice, false)
  } catch (error) {
    if (error instanceof ArchiveLimitError) throw error
    throw new ArchiveLimitError()
  }
  if (!inflator.ended || inflator.err || inflator.strm.avail_in !== 0) throw new ArchiveLimitError()
  return total
}

function sameBytes(left, right) {
  if (left.length !== right.length) return false
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) return false
  }
  return true
}

function u16(bytes, offset) {
  return bytes[offset] | (bytes[offset + 1] << 8)
}

function u32(bytes, offset) {
  return (bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24)) >>> 0
}
