import { readFileSync, writeFileSync } from 'node:fs'
import { dirname, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const webRoot = resolve(here, '../../..')
const repoRoot = resolve(webRoot, '..')
const args = process.argv.slice(2)
const option = name => {
  const index = args.indexOf(name)
  return index >= 0 ? args[index + 1] : undefined
}
const allowIncomplete = args.includes('--allow-incomplete')
const requirePassed = args.includes('--require-passed')
const reportFile = resolve(repoRoot, option('--report') ?? '.artifacts/tests/frontend-migration/vitest-first.json')

const readJson = file => JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, ''))
const previousFile = resolve(here, 'previous-cases.json')
const authMapFile = resolve(webRoot, 'src/test/migrated/auth/legacy-title-map.json')
const filesMapFile = resolve(webRoot, 'src/test/migrated/files/migration-map.json')
const manualMapFile = resolve(here, 'manual-mappings.json')
const previous = readJson(previousFile)
const authTitles = readJson(authMapFile)
const filesMappings = readJson(filesMapFile)
const manualMappings = readJson(manualMapFile)
const vitest = readJson(reportFile)

const portable = value => {
  const normalized = value.replaceAll('\\', '/')
  const marker = normalized.toLowerCase().lastIndexOf('/web/')
  if (marker >= 0) return `web/${normalized.slice(marker + 5)}`
  if (normalized.startsWith('web/')) return normalized
  if (normalized.startsWith('src/')) return `web/${normalized}`
  return normalized
}
const oldKey = value => `${value.oldFile ?? value.file}:${value.oldLine ?? value.line}:${value.oldTitle ?? value.title}`
const targetKey = value => `${portable(value.newFile ?? value.file)}:${value.newTitle ?? value.title}`
const previousCases = Object.entries(previous).flatMap(([group, cases]) =>
  cases.map(item => ({ group, ...item, id: oldKey(item) })))
const filesByOld = new Map(filesMappings.map(item => [oldKey(item), item]))
const manualByOld = new Map(manualMappings.map(item => [oldKey(item), item]))

const reportedTests = vitest.testResults.flatMap(result =>
  (result.assertionResults ?? []).map(assertion => ({
    file: portable(result.name),
    title: assertion.title,
    fullName: assertion.fullName,
    status: assertion.status,
  })))
const testsByTitle = new Map()
const testsByTarget = new Map()
for (const test of reportedTests) {
  const titled = testsByTitle.get(test.title) ?? []
  titled.push(test)
  testsByTitle.set(test.title, titled)
  const targeted = testsByTarget.get(targetKey(test)) ?? []
  targeted.push(test)
  testsByTarget.set(targetKey(test), targeted)
}

function explicitTargets(mapping) {
  if (!mapping) return undefined
  if (Array.isArray(mapping.newCases)) return mapping.newCases
  return [{ newFile: mapping.newFile, newTitle: mapping.newTitle }]
}

const entries = previousCases.map(legacy => {
  const manual = manualByOld.get(legacy.id)
  const files = filesByOld.get(legacy.id)
  const targets = explicitTargets(manual ?? files)
  let resolution = manual ? 'manual' : files ? 'files-map' : 'exact-title'
  let candidates
  if (targets) {
    candidates = targets.flatMap(target => testsByTarget.get(targetKey(target)) ?? [])
  } else {
    const expectedTitle = legacy.group === 'auth' ? authTitles[legacy.title] : undefined
    if (expectedTitle !== undefined) resolution = expectedTitle === legacy.title ? 'auth-exact' : 'auth-title-map'
    candidates = testsByTitle.get(expectedTitle ?? legacy.title) ?? []
  }
  const unique = [...new Map(candidates.map(item => [targetKey(item), item])).values()]
  return {
    legacy: {
      id: legacy.id,
      group: legacy.group,
      file: legacy.file,
      line: legacy.line,
      title: legacy.title,
    },
    resolution,
    note: manual?.note,
    migrated: unique,
  }
})

const unresolved = entries.filter(entry => entry.migrated.length === 0)
const ambiguous = entries.filter(entry =>
  entry.resolution !== 'manual' && entry.migrated.length > 1)
const duplicateLegacyIds = previousCases.filter((item, index, all) =>
  all.findIndex(candidate => candidate.id === item.id) !== index)
const unexpectedFileMappings = filesMappings.filter(item => !previousCases.some(old => old.id === oldKey(item)))
const unexpectedManualMappings = manualMappings.filter(item => !previousCases.some(old => old.id === oldKey(item)))
const targetUsage = new Map()
for (const entry of entries) {
  for (const target of entry.migrated) {
    const key = targetKey(target)
    const users = targetUsage.get(key) ?? []
    users.push(entry.legacy.id)
    targetUsage.set(key, users)
  }
}
const sharedTargets = [...targetUsage.entries()]
  .filter(([, users]) => users.length > 1)
  .map(([target, legacyIds]) => ({ target, legacyIds }))
const failedTargets = entries.flatMap(entry => entry.migrated
  .filter(target => target.status !== 'passed')
  .map(target => ({ legacyId: entry.legacy.id, ...target })))

const resolutionCounts = Object.fromEntries([...new Set(entries.map(entry => entry.resolution))]
  .sort().map(name => [name, entries.filter(entry => entry.resolution === name).length]))
const groupCounts = Object.fromEntries(Object.entries(previous).map(([group, cases]) => [group, cases.length]))
const reportPath = portable(relative(repoRoot, reportFile))
const summary = {
  schemaVersion: 1,
  source: {
    file: 'web/src/test/migration/previous-cases.json',
    total: previousCases.length,
    groups: groupCounts,
  },
  vitestSnapshot: {
    file: reportPath,
    total: vitest.numTotalTests,
    passed: vitest.numPassedTests,
    failed: vitest.numFailedTests,
    success: vitest.success,
  },
  coverage: {
    mappedLegacyCases: entries.length - unresolved.length,
    unresolvedLegacyCases: unresolved.length,
    ambiguousLegacyCases: ambiguous.length,
    uniqueMigratedTargets: targetUsage.size,
    failedMappedTargets: failedTargets.length,
    resolutionCounts,
  },
  integrity: {
    duplicateLegacyIds: duplicateLegacyIds.map(item => item.id),
    unexpectedFileMappings: unexpectedFileMappings.map(oldKey),
    unexpectedManualMappings: unexpectedManualMappings.map(oldKey),
    sharedTargets,
  },
  unresolved: unresolved.map(entry => entry.legacy),
  ambiguous: ambiguous.map(entry => ({ legacy: entry.legacy, candidates: entry.migrated })),
  failedTargets,
}

const mapDocument = {
  schemaVersion: 1,
  sourceFile: 'web/src/test/migration/previous-cases.json',
  sourceCount: previousCases.length,
  vitestSnapshot: reportPath,
  entries,
}
writeFileSync(resolve(here, 'legacy-case-map.json'), `${JSON.stringify(mapDocument, null, 2)}\n`)
writeFileSync(resolve(here, 'migration-report.json'), `${JSON.stringify(summary, null, 2)}\n`)

const markdown = `# Frontend legacy test migration coverage

This report maps each concrete legacy Node test registration to the Vitest test that replaces it. The source list expands dynamic registrations, so its total is the legacy runtime total rather than the number of static \`test(...)\` calls.

| Metric | Result |
|---|---:|
| Legacy cases | ${summary.source.total} |
| Mapped legacy cases | ${summary.coverage.mappedLegacyCases} |
| Unresolved legacy cases | ${summary.coverage.unresolvedLegacyCases} |
| Ambiguous legacy cases | ${summary.coverage.ambiguousLegacyCases} |
| Unique migrated targets | ${summary.coverage.uniqueMigratedTargets} |
| Mapped targets not passed in snapshot | ${summary.coverage.failedMappedTargets} |

Vitest snapshot: \`${summary.vitestSnapshot.file}\` (${summary.vitestSnapshot.passed}/${summary.vitestSnapshot.total} passed).

## Resolution methods

${Object.entries(resolutionCounts).map(([name, count]) => `- \`${name}\`: ${count}`).join('\n')}

## Unresolved cases

${summary.unresolved.length ? summary.unresolved.map(item => `- \`${item.id}\``).join('\n') : 'None.'}

## Ambiguous cases

${summary.ambiguous.length ? summary.ambiguous.map(item => `- \`${item.legacy.id}\``).join('\n') : 'None.'}

## Maintenance

Regenerate after a Vitest JSON run:

\`node src/test/migration/build-migration-report.mjs --report .artifacts/tests/frontend-migration/vitest-final.json --require-passed\`

Same-title cases resolve automatically. Authentication renames come from \`migrated/auth/legacy-title-map.json\`; file migrations use \`migrated/files/migration-map.json\`; cross-domain, split, or otherwise renamed cases belong in \`manual-mappings.json\`.
`
writeFileSync(resolve(here, 'MIGRATION_REPORT.md'), markdown)

const structuralFailures = duplicateLegacyIds.length + unexpectedFileMappings.length
  + unexpectedManualMappings.length + unresolved.length + ambiguous.length
const passFailures = requirePassed ? failedTargets.length : 0
const snapshotFailures = requirePassed && !vitest.success ? 1 : 0
console.log(JSON.stringify(summary, null, 2))
if (!allowIncomplete && (previousCases.length !== 299 || entries.length !== 299
    || structuralFailures + passFailures + snapshotFailures > 0)) {
  process.exitCode = 1
}
