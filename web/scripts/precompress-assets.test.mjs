import assert from 'node:assert/strict'
import { randomBytes } from 'node:crypto'
import { mkdtemp, readFile, rm, unlink, writeFile } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { precompressAssets, verifyPrecompressedAssets } from './precompress-assets.mjs'

async function fixture() {
  const root = await mkdtemp(path.join(os.tmpdir(), 'yf-precompress-'))
  await writeFile(path.join(root, 'index.html'), `<!doctype html>${'<main>content</main>'.repeat(400)}`)
  await writeFile(path.join(root, 'small.json'), '{}')
  await writeFile(path.join(root, 'font.woff2'), randomBytes(2048))
  return root
}

test('manifest covers sources and verification rejects corrupt, missing, and stale companions', async t => {
  const root = await fixture()
  t.after(() => rm(root, { recursive: true, force: true }))

  let manifest = await precompressAssets(root)
  assert.deepEqual(manifest.assets.map(asset => asset.path), ['font.woff2', 'index.html', 'small.json'])
  assert.equal(manifest.assets.find(asset => asset.path === 'font.woff2').encodings.br.status, 'skipped')
  assert.equal(manifest.assets.find(asset => asset.path === 'small.json').encodings.br.status, 'skipped')
  assert.equal(manifest.assets.find(asset => asset.path === 'index.html').encodings.br.status, 'generated')
  await verifyPrecompressedAssets(root)

  const html = manifest.assets.find(asset => asset.path === 'index.html')
  const br = path.join(root, html.encodings.br.path)
  const originalBr = await readFile(br)
  await writeFile(br, Buffer.concat([originalBr, Buffer.from('corrupt')]))
  await assert.rejects(verifyPrecompressedAssets(root), /corrupt precompressed representation/)

  manifest = await precompressAssets(root)
  const generated = manifest.assets.find(asset => asset.path === 'index.html')
  await unlink(path.join(root, generated.encodings.gzip.path))
  await assert.rejects(verifyPrecompressedAssets(root), /Missing, stale, or corrupt|Orphan or missing/)

  await precompressAssets(root)
  await writeFile(path.join(root, 'index.html'), 'changed after compression')
  await assert.rejects(verifyPrecompressedAssets(root), /Stale precompression source metadata/)
})
