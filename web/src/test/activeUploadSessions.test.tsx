import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  get: vi.fn(),
  abortUpload: vi.fn(),
}))

vi.mock('../api/client', () => ({
  default: { get: mocks.get },
}))

vi.mock('../api/uploads', () => ({
  abortUpload: mocks.abortUpload,
}))

vi.mock('../store/collaboration', () => ({
  useCollaboration: (selector: (state: { activityRevisions: Record<number, number> }) => unknown) => selector({ activityRevisions: {} }),
}))

import ActiveUploadSessions from '../components/ActiveUploadSessions'

const sessionItem = (sessionId: string, fileName: string) => ({
  sessionId,
  fileName,
  status: 'UPLOADING',
  receivedChunks: 1,
  totalChunks: 4,
  fileSize: 4096,
  uploaderName: '工程师甲',
  canResume: false,
  canAbort: true,
})

describe('ActiveUploadSessions', () => {
  beforeEach(() => {
    mocks.get.mockReset()
    mocks.abortUpload.mockReset()
  })

  afterEach(() => {
    document.querySelectorAll('.arco-trigger-popup').forEach((node) => node.remove())
  })

  it('does not open DangerPopconfirm on disabled or busy abort buttons while an abort is in progress', async () => {
    let resolveAbort!: () => void
    const first = sessionItem('sess-1', '图纸A.step')
    const second = sessionItem('sess-2', '图纸B.step')
    mocks.get
      .mockResolvedValueOnce({ data: { total: 2, items: [first, second] } })
      .mockResolvedValueOnce({ data: { total: 1, items: [second] } })
    mocks.abortUpload.mockReturnValue(new Promise<void>((resolve) => { resolveAbort = resolve }))

    render(<ActiveUploadSessions projectId={11} />)
    const row1 = (await screen.findByText('图纸A.step')).closest('li')!
    const row2 = screen.getByText('图纸B.step').closest('li')!

    const getCancel = (row: HTMLElement) => within(row).getByRole('button', { name: '取消' })

    fireEvent.click(getCancel(row1))
    const popup = await screen.findByRole('tooltip')
    fireEvent.click(within(popup).getByRole('button', { name: '确定' }))

    await waitFor(() => expect(getCancel(row2)).toBeDisabled())
    fireEvent.click(within(popup).getByRole('button', { name: '取消' }))
    await waitFor(() => expect(screen.queryByRole('tooltip')).not.toBeInTheDocument())

    // Clicking either the sibling disabled button (or its Arco Trigger wrapper) or the loading button must not open a popconfirm.
    const disabledCancel2 = getCancel(row2)
    fireEvent.click(disabledCancel2)
    if (disabledCancel2.parentElement) fireEvent.click(disabledCancel2.parentElement)
    fireEvent.click(getCancel(row1))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()

    resolveAbort()
    await waitFor(() => expect(screen.queryByText('图纸A.step')).not.toBeInTheDocument())
    await waitFor(() => expect(within(screen.getByText('图纸B.step').closest('li')!).getByRole('button', { name: '取消' })).toBeEnabled())

    fireEvent.click(within(screen.getByText('图纸B.step').closest('li')!).getByRole('button', { name: '取消' }))
    expect(await screen.findByRole('tooltip')).toHaveTextContent('取消后已上传的分片会被丢弃，确认取消？')
  })
})
