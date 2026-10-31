import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

vi.mock('../../../api/client', () => ({ default: { get: mocks.get, post: mocks.post } }))

import StorageLocationsPanel from '../../../pages/system/components/StorageLocationsPanel'

function location(overrides: Record<string, unknown>) {
  return {
    kind: 'COLLAB', label: '协作文件存储', path: 'D:\\YfData\\storage', previousPath: null, pendingPath: null,
    status: 'IDLE', phase: null, totalFiles: 0, totalBytes: 0, copiedFiles: 0, copiedBytes: 0, deletedFiles: 0,
    skippedFiles: 0, lastError: null, startedAt: null, completedAt: null, freeBytes: 1024 * 1024 * 1024,
    ...overrides,
  }
}

const idle = [
  location({}),
  location({ kind: 'OEM', label: 'OEM 文件存储', path: '', freeBytes: null }),
  location({ kind: 'LOG', label: '日志目录', path: 'D:\\YfData\\logs' }),
]

describe('StorageLocationsPanel', () => {
  beforeEach(() => {
    mocks.get.mockReset()
    mocks.post.mockReset()
  })

  it('starts a migration with the typed path and shows its progress', async () => {
    const migrating = [
      location({
        path: 'E:\\YfData\\storage', previousPath: 'D:\\YfData\\storage', status: 'MIGRATING', phase: 'COPYING',
        totalFiles: 10, totalBytes: 1000, copiedFiles: 4, copiedBytes: 400,
      }),
      idle[1], idle[2],
    ]
    mocks.get.mockResolvedValueOnce({ data: { items: idle } }).mockResolvedValue({ data: { items: migrating } })
    mocks.post.mockResolvedValue({ data: migrating[0] })
    const user = userEvent.setup()
    render(<StorageLocationsPanel />)

    const row = (await screen.findByText('协作文件存储')).closest('tr')!
    expect(within(row).getByText('正常')).toBeInTheDocument()
    expect(within(screen.getByText('OEM 文件存储').closest('tr')!).getByText('未配置')).toBeInTheDocument()
    await user.click(within(row).getByRole('button', { name: '更换路径' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('button', { name: '开始迁移' })).toBeDisabled()
    fireEvent.change(within(dialog).getByLabelText('新目录路径'), { target: { value: ' E:\\YfData\\storage ' } })
    await user.click(within(dialog).getByRole('button', { name: '开始迁移' }))

    await waitFor(() => expect(mocks.post).toHaveBeenCalledWith(
      '/admin/storage-locations/COLLAB/migrate', { path: 'E:\\YfData\\storage' }))
    const updated = (await screen.findByText('复制校验中')).closest('tr')!
    expect(within(updated).getByText('原目录：D:\\YfData\\storage')).toBeInTheDocument()
    expect(within(updated).getByText(/4\/10 个文件/)).toBeInTheDocument()
    expect(within(updated).getByRole('button', { name: '取消迁移' })).toBeInTheDocument()
    // Another location cannot start while one migrates.
    expect(within(screen.getByText('日志目录').closest('tr')!).getByRole('button', { name: '更换路径' })).toBeDisabled()
  })

  it('offers a retry for a failed migration and shows the failure reason', async () => {
    mocks.get.mockResolvedValue({
      data: {
        items: [
          location({ previousPath: 'C:\\old', status: 'FAILED', phase: 'COPYING', lastError: '目标磁盘空间不足' }),
          idle[1], idle[2],
        ],
      },
    })
    mocks.post.mockResolvedValue({ data: {} })
    const user = userEvent.setup()
    render(<StorageLocationsPanel />)

    expect(await screen.findByText('协作文件存储：目标磁盘空间不足')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '重试' }))
    await waitFor(() => expect(mocks.post).toHaveBeenCalledWith('/admin/storage-locations/COLLAB/retry'))
  })
})
