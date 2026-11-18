#!/usr/bin/env node
// Dependency audit with a reviewed allowlist.
//
// `npm audit` cannot express "this advisory has no fix yet", so CI either goes red on an advisory the
// maintainers cannot fix, or the whole step gets deleted and future advisories go unnoticed. This script
// keeps the step meaningful: every reported advisory must either be fixed or listed in
// `audit-allowlist.json` with a reason and a review date. Anything else fails the build.
//
// Usage: node scripts/audit-deps.mjs [--strict]
//   --strict  ignore the allowlist (use it to confirm the gate still catches the known advisory)
import { execSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const allowlistPath = join(here, 'audit-allowlist.json')
const strict = process.argv.includes('--strict')

function loadAllowlist() {
  const raw = JSON.parse(readFileSync(allowlistPath, 'utf8'))
  return new Map((raw.advisories ?? []).map((item) => [item.ghsa.toUpperCase(), item]))
}

function runAudit() {
  // A single command string keeps Windows (`npm.cmd`) and Linux working without shell-escaping arguments.
  try {
    return JSON.parse(execSync('npm audit --include=dev --json', {
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
      stdio: ['ignore', 'pipe', 'pipe'],
    }))
  } catch (error) {
    // npm exits non-zero when it finds vulnerabilities; the JSON report is still on stdout.
    if (typeof error.stdout === 'string' && error.stdout.trim().startsWith('{')) return JSON.parse(error.stdout)
    throw error
  }
}

const report = runAudit()
const advisories = new Map()
for (const [name, entry] of Object.entries(report.vulnerabilities ?? {})) {
  for (const via of entry.via ?? []) {
    if (typeof via !== 'object' || !via.url) continue
    const id = (via.url.match(/GHSA-[a-z0-9-]+/i)?.[0] ?? via.url).toUpperCase()
    if (!advisories.has(id)) {
      advisories.set(id, { id, title: via.title ?? '', severity: via.severity ?? 'unknown', url: via.url, packages: new Set() })
    }
    advisories.get(id).packages.add(name)
  }
}

const allowlist = loadAllowlist()
const today = new Date().toISOString().slice(0, 10)
const warnings = []
const blocked = []
for (const advisory of advisories.values()) {
  const entry = strict ? undefined : allowlist.get(advisory.id)
  const packages = [...advisory.packages].sort().join(', ')
  if (entry) {
    const expired = entry.reviewBy && entry.reviewBy < today
    console.log(`ALLOWED ${advisory.id} (${advisory.severity}) ${advisory.title} [${packages}]`)
    if (expired) warnings.push(`${advisory.id}: allowlist entry passed its review date ${entry.reviewBy}`)
    continue
  }
  blocked.push(`${advisory.id} (${advisory.severity}) ${advisory.title} [${packages}] ${advisory.url}`)
}

for (const item of (report.vulnerabilities ? Object.entries(report.vulnerabilities) : [])) {
  const [name, entry] = item
  const fixed = entry.fixAvailable
  if (fixed && blocked.some((text) => text.includes(name))) {
    console.log(`FIX AVAILABLE ${name}: ${typeof fixed === 'object' ? `${fixed.name}@${fixed.version} (semver major: ${fixed.isSemVerMajor})` : 'npm audit fix'}`)
  }
}

if (warnings.length > 0) {
  for (const warning of warnings) console.warn(`WARN ${warning}`)
}
if (blocked.length > 0) {
  console.error(`FAIL ${blocked.length} advisory(ies) are neither fixed nor allowlisted:`)
  for (const text of blocked) console.error(`  - ${text}`)
  process.exit(1)
}

const total = advisories.size
const allowedCount = allowedEntriesUsed(allowlist, advisories)
console.log(`OK ${total === 0 ? 'no advisories reported' : `${allowedCount}/${total} advisories allowlisted, none outstanding`}`)

function allowedEntriesUsed(list, found) {
  let count = 0
  for (const id of found.keys()) if (list.has(id)) count += 1
  return count
}
