import { createHash } from 'node:crypto'
import { lstat, mkdir, readFile, readdir, unlink, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { brotliCompressSync, brotliDecompressSync, constants, gunzipSync, gzipSync } from 'node:zlib'

const manifestName = '.precompressed-assets.json'
const eligibleExtensions = new Set([
  '.css', '.eot', '.html', '.js', '.json', '.map', '.mjs', '.otf', '.pfb', '.svg',
  '.ttf', '.txt', '.wasm', '.webmanifest', '.woff', '.woff2', '.xml',
])
const minimumSourceBytes = 256
const minimumSavingsBytes = 32
const minimumSavingsRatio = 0.03

function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex')
}

function relativePath(root, absolute) {
  return path.relative(root, absolute).split(path.sep).join('/')
}

function worthwhile(sourceLength, compressedLength) {
  const required = Math.max(minimumSavingsBytes, Math.ceil(sourceLength * minimumSavingsRatio))
  return sourceLength >= minimumSourceBytes && sourceLength - compressedLength >= required
}

async function walkFiles(root, directory = root) {
  const files = []
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const absolute = path.join(directory, entry.name)
    if (entry.isSymbolicLink()) throw new Error(`Linked build output is not allowed: ${relativePath(root, absolute)}`)
    if (entry.isDirectory()) files.push(...await walkFiles(root, absolute))
    else if (entry.isFile()) files.push(absolute)
  }
  return files
}

function compressionResult(source, compressed, outputPath) {
  if (!worthwhile(source.length, compressed.length)) {
    return { status: 'skipped', reason: source.length < minimumSourceBytes ? 'source-too-small' : 'not-worthwhile', candidateBytes: compressed.length }
  }
  return {
    status: 'generated',
    path: outputPath,
    bytes: compressed.length,
    sha256: sha256(compressed),
  }
}

export async function precompressAssets(outputDirectory) {
  const root = path.resolve(outputDirectory)
  const rootInfo = await lstat(root)
  if (!rootInfo.isDirectory() || rootInfo.isSymbolicLink()) throw new Error(`Build output must be a real directory: ${root}`)

  const allFiles = await walkFiles(root)
  for (const absolute of allFiles) {
    const relative = relativePath(root, absolute)
    if (relative === manifestName) await unlink(absolute)
    else if (/\.(?:br|gz)$/i.test(relative) && eligibleExtensions.has(path.extname(relative.slice(0, relative.lastIndexOf('.'))).toLowerCase())) {
      await unlink(absolute)
    }
  }

  const sourceFiles = (await walkFiles(root))
    .filter(absolute => eligibleExtensions.has(path.extname(absolute).toLowerCase()))
    .sort((left, right) => relativePath(root, left).localeCompare(relativePath(root, right)))
  if (!sourceFiles.some(file => relativePath(root, file) === 'index.html')) {
    throw new Error('Build output is missing index.html')
  }

  const assets = []
  for (const absolute of sourceFiles) {
    const relative = relativePath(root, absolute)
    const source = await readFile(absolute)
    const brotli = brotliCompressSync(source, {
      params: {
        [constants.BROTLI_PARAM_QUALITY]: 11,
        [constants.BROTLI_PARAM_MODE]: /\.(?:html|css|js|json|map|mjs|svg|txt|webmanifest|xml)$/i.test(relative)
          ? constants.BROTLI_MODE_TEXT
          : constants.BROTLI_MODE_GENERIC,
      },
    })
    const gzip = gzipSync(source, { level: 9, mtime: 0 })
    const brPath = `${relative}.br`
    const gzipPath = `${relative}.gz`
    const br = compressionResult(source, brotli, brPath)
    const gz = compressionResult(source, gzip, gzipPath)
    if (br.status === 'generated') {
      await mkdir(path.dirname(`${absolute}.br`), { recursive: true })
      await writeFile(`${absolute}.br`, brotli)
    }
    if (gz.status === 'generated') {
      await mkdir(path.dirname(`${absolute}.gz`), { recursive: true })
      await writeFile(`${absolute}.gz`, gzip)
    }
    assets.push({
      path: relative,
      bytes: source.length,
      sha256: sha256(source),
      encodings: { br, gzip: gz },
    })
  }

  const manifest = {
    schemaVersion: 1,
    root: 'wwwroot',
    policy: {
      eligibleExtensions: [...eligibleExtensions].sort(),
      minimumSourceBytes,
      minimumSavingsBytes,
      minimumSavingsRatio,
      brotliQuality: 11,
      gzipLevel: 9,
    },
    assets,
  }
  await writeFile(path.join(root, manifestName), `${JSON.stringify(manifest, null, 2)}\n`, 'utf8')
  return manifest
}

export async function verifyPrecompressedAssets(outputDirectory, manifestPath = path.join(outputDirectory, manifestName)) {
  const root = path.resolve(outputDirectory)
  const manifest = JSON.parse(await readFile(path.resolve(manifestPath), 'utf8'))
  if (manifest.schemaVersion !== 1 || manifest.root !== 'wwwroot'
    || JSON.stringify(manifest.policy?.eligibleExtensions) !== JSON.stringify([...eligibleExtensions].sort())
    || manifest.policy?.minimumSourceBytes !== minimumSourceBytes
    || manifest.policy?.minimumSavingsBytes !== minimumSavingsBytes
    || manifest.policy?.minimumSavingsRatio !== minimumSavingsRatio
    || manifest.policy?.brotliQuality !== 11 || manifest.policy?.gzipLevel !== 9
    || !Array.isArray(manifest.assets)) throw new Error('Unsupported precompression manifest policy')

  const files = await walkFiles(root)
  const actualSources = files
    .map(absolute => relativePath(root, absolute))
    .filter(relative => relative !== manifestName && !/\.(?:br|gz)$/i.test(relative)
      && eligibleExtensions.has(path.extname(relative).toLowerCase()))
    .sort()
  const manifestSources = manifest.assets.map(asset => asset.path)
  if (new Set(manifestSources).size !== manifestSources.length
    || JSON.stringify([...manifestSources].sort()) !== JSON.stringify(actualSources)) {
    throw new Error('Precompression manifest does not cover the exact eligible public asset set')
  }

  const expectedCompanions = new Set()
  for (const asset of manifest.assets) {
    if (path.posix.isAbsolute(asset.path) || asset.path.split('/').some(segment => segment === '..'))
      throw new Error(`Unsafe precompression asset path: ${asset.path}`)
    const source = await readFile(path.join(root, ...asset.path.split('/')))
    if (asset.bytes !== source.length || asset.sha256 !== sha256(source))
      throw new Error(`Stale precompression source metadata: ${asset.path}`)
    const compressedCandidates = {
      br: brotliCompressSync(source, {
        params: {
          [constants.BROTLI_PARAM_QUALITY]: 11,
          [constants.BROTLI_PARAM_MODE]: /\.(?:html|css|js|json|map|mjs|svg|txt|webmanifest|xml)$/i.test(asset.path)
            ? constants.BROTLI_MODE_TEXT
            : constants.BROTLI_MODE_GENERIC,
        },
      }),
      gzip: gzipSync(source, { level: 9, mtime: 0 }),
    }
    for (const [encoding, suffix] of [['br', '.br'], ['gzip', '.gz']]) {
      const entry = asset.encodings?.[encoding]
      const expected = compressionResult(source, compressedCandidates[encoding], `${asset.path}${suffix}`)
      if (!entry || entry.status !== expected.status || entry.reason !== expected.reason
        || entry.candidateBytes !== expected.candidateBytes || entry.path !== expected.path
        || entry.bytes !== expected.bytes || entry.sha256 !== expected.sha256) {
        throw new Error(`Stale precompression decision: ${asset.path} (${encoding})`)
      }
      const companionPath = path.join(root, ...`${asset.path}${suffix}`.split('/'))
      if (entry.status === 'generated') {
        expectedCompanions.add(`${asset.path}${suffix}`)
        let compressed
        try { compressed = await readFile(companionPath) }
        catch (error) {
          if (error?.code === 'ENOENT') throw new Error(`Missing, stale, or corrupt precompressed representation: ${entry.path}`)
          throw error
        }
        if (compressed.length !== entry.bytes || sha256(compressed) !== entry.sha256)
          throw new Error(`Missing, stale, or corrupt precompressed representation: ${entry.path}`)
        let restored
        try { restored = encoding === 'br' ? brotliDecompressSync(compressed) : gunzipSync(compressed) }
        catch { throw new Error(`Precompressed representation cannot be decompressed: ${entry.path}`) }
        if (!restored.equals(source)) throw new Error(`Precompressed representation differs from source: ${entry.path}`)
      } else {
        try { await lstat(companionPath); throw new Error(`Unexpected skipped representation: ${asset.path}${suffix}`) }
        catch (error) { if (error?.code !== 'ENOENT') throw error }
      }
    }
  }
  const actualCompanions = files.map(absolute => relativePath(root, absolute))
    .filter(relative => /\.(?:br|gz)$/i.test(relative)).sort()
  if (JSON.stringify([...expectedCompanions].sort()) !== JSON.stringify(actualCompanions))
    throw new Error('Orphan or missing precompressed representation')
  return manifest
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const verify = process.argv[2] === '--verify'
  const outputDirectory = (verify ? process.argv[3] : process.argv[2])
    ?? path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'dist')
  const manifest = verify
    ? await verifyPrecompressedAssets(outputDirectory, process.argv[4])
    : await precompressAssets(outputDirectory)
  const generated = manifest.assets.reduce((count, asset) => count
    + Number(asset.encodings.br.status === 'generated')
    + Number(asset.encodings.gzip.status === 'generated'), 0)
  process.stdout.write(`${verify ? 'Verified' : 'Precompressed'} ${generated} representations for ${manifest.assets.length} public assets.\n`)
}
