import { describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ post: vi.fn() }))
vi.mock('../../../api/client', () => ({ default: { post: mocks.post } }))

import { createProjectCopyJob, isCopyJob, unwrapCopyJob } from '../../../api/copyJobs'
import type { ProjectCopyJob } from '../../../api/types'

const validJob: ProjectCopyJob = {
  jobId: 7,
  sourceProjectId: 9,
  projectGroupId: 3,
  targetName: '复制件',
  status: 'succeeded',
  filesTotal: 2,
  filesCopied: 2,
  bytesTotal: 100,
  bytesCopied: 100,
  error: null,
  result: { projectId: 10, copyFileCount: 2 },
  createdAt: '2026-09-23T00:00:00Z',
  startedAt: '2026-09-23T00:00:01Z',
  completedAt: '2026-09-23T00:00:02Z',
}

describe('copy job response validation', () => {
  it('accepts the flat backend response and rejects an old envelope', () => {
    expect(isCopyJob(validJob)).toBe(true)
    expect(unwrapCopyJob(validJob)).toEqual(validJob)
    expect(isCopyJob({ job: validJob })).toBe(false)
    expect(() => unwrapCopyJob({ job: validJob })).toThrow('响应格式无效')
  })

  it('rejects unsafe progress and incomplete succeeded records', () => {
    expect(isCopyJob({ ...validJob, jobId: Number.NaN })).toBe(false)
    expect(isCopyJob({ ...validJob, filesCopied: 3 })).toBe(false)
    expect(isCopyJob({ ...validJob, status: 'succeeded', result: null })).toBe(false)
  })

  it('uses Unicode code points for the request name limit and rejects unsafe source ids', async () => {
    mocks.post.mockResolvedValue({ data: validJob })
    const name = '😀'.repeat(128)
    await createProjectCopyJob(9, { name, idempotencyKey: 'stable-key' })
    expect(mocks.post).toHaveBeenCalledWith('/projects/9/copy', { name, idempotencyKey: 'stable-key' }, expect.anything())
    expect(() => createProjectCopyJob(9, { name: `${name}😀`, idempotencyKey: 'stable-key' })).toThrow('名称')
    expect(() => createProjectCopyJob(Number.NaN, { name: '有效名称', idempotencyKey: 'stable-key' })).toThrow('来源')
  })
})
