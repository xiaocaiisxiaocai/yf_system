import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import ProjectCopyJobsPanel from '../../../components/project-copy-jobs/ProjectCopyJobsPanel'
import type { ProjectCopyJob } from '../../../api/types'

function job(overrides: Partial<ProjectCopyJob> = {}): ProjectCopyJob {
  return {
    jobId: 1,
    sourceProjectId: 9,
    projectGroupId: 3,
    targetName: '后台副本',
    status: 'running',
    filesTotal: 4,
    filesCopied: 2,
    bytesTotal: 2048,
    bytesCopied: 1024,
    error: null,
    result: null,
    createdAt: '2026-09-23T00:00:00Z',
    startedAt: '2026-09-23T00:00:01Z',
    completedAt: null,
    ...overrides,
  }
}

describe('ProjectCopyJobsPanel', () => {
  it('renders running progress, failures, and a safe success entry point', async () => {
    const openResult = vi.fn()
    const user = userEvent.setup()
    render(
      <ProjectCopyJobsPanel
        jobs={[
          job({ filesTotal: 0, filesCopied: 0, bytesTotal: 2048, bytesCopied: 1024 }),
          job({ jobId: 2, status: 'failed', error: '目标名称已存在' }),
          job({ jobId: 3, status: 'succeeded', result: { projectId: 88, copyFileCount: 4 }, filesCopied: 4, bytesCopied: 2048 }),
        ]}
        loading={false}
        error={false}
        onRetry={vi.fn()}
        onOpenResult={openResult}
      />,
    )

    expect(screen.getByText('复制进行中')).toBeInTheDocument()
    expect(screen.getByText(/已复制 1.0 KB \/ 2.0 KB/)).toBeInTheDocument()
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50')
    expect(screen.getByText('目标名称已存在')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '进入副本' }))
    expect(openResult).toHaveBeenCalledWith(88)
  })
})
