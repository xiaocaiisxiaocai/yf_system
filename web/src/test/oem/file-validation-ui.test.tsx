import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../../api/queryClient'
import { OemProvider } from '../../oem/OemContext'
import type { OemApi } from '../../oem/api/OemApi'
import type { SettingItem, TransferDetail } from '../../oem/api/types'
import TransferDetailPage from '../../oem/pages/TransferDetailPage'
import { SettingsPage } from '../../oem/pages/admin/PolicyPages'

function renderWithOem(element: React.ReactNode, api: Partial<OemApi>, permissions: string[] = []) {
  return render(
    <QueryClientProvider client={createAppQueryClient()}>
      <OemProvider value={{
        api: api as OemApi,
        realm: 'internal',
        base: '/oem',
        permissions: new Set(permissions),
        userId: 7,
        queryScope: ['test'],
      }}>
        {element}
      </OemProvider>
    </QueryClientProvider>,
  )
}

describe('OEM 文件校验界面', () => {
  it('设置页显示文件与压缩结构规则，并过滤旧病毒扫描参数', async () => {
    const settings: SettingItem[] = [
      {
        key: 'oem.validation.archive_max_depth',
        label: '压缩包最大嵌套层级',
        kind: 'integer',
        value: '5',
        min: 1,
        max: 20,
        hint: '超出层级的压缩包将判定为无效',
      },
      {
        key: 'oem.scan.max_signature_age_hours',
        label: '病毒库最长未更新时间（小时）',
        kind: 'integer',
        value: '48',
        min: 1,
        max: 2160,
      },
      {
        key: 'oem.scan.block_on_stale_signatures',
        label: '病毒库过期时暂停放行',
        kind: 'boolean',
        value: 'false',
        min: null,
        max: null,
      },
    ]
    const api = {
      settings: vi.fn().mockResolvedValue(settings),
      updateSettings: vi.fn(),
    }

    renderWithOem(<SettingsPage />, api, ['oem:file_policy_manage'])

    expect(await screen.findByLabelText('压缩包最大嵌套层级')).toHaveValue('5')
    expect(screen.getByText('按需设置文件类型、上传容量、压缩包结构及下载限制。')).toBeInTheDocument()
    expect(screen.queryByText(/病毒|扫描/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '保存更改' })).toBeDisabled()
  })

  it('详情显示服务端返回的真实校验失败原因和暂存状态', async () => {
    const detail: TransferDetail = {
      summary: {
        id: 44,
        direction: 'OEM_TO_INTERNAL',
        companyId: 9,
        companyName: '测试代工厂',
        title: '待处理图纸',
        sender: { realm: 'oem', id: 31, employeeNo: 'OEM31', realName: '厂商用户' },
        lifecycleStatus: 'DRAFT',
        approvalStatus: null,
        approvalBlockedReason: null,
        validationSummary: 'INVALID',
        fileCount: 1,
        totalBytes: 1024,
        availableCount: 0,
        purgePendingCount: 0,
        purgedCount: 0,
        missingCount: 0,
        createdAt: '2026-10-05T00:00:00Z',
        sentAt: null,
        releasedAt: null,
        version: 1,
      },
      description: null,
      retention: {
        templateId: 1,
        templateName: '保留七天',
        mode: 'AFTER_RELEASE',
        releaseTtlMinutes: 10080,
        receiptGraceMinutes: null,
        summary: '发布后七天删除',
      },
      manifestSha256: null,
      expiresAt: null,
      closedReason: null,
      closedAt: null,
      capabilities: { canEdit: false, canSend: false, canDelete: false, canReadContent: true, contentPurpose: 'REVIEW' },
      files: [{
        id: 301,
        originalName: '图纸.zip',
        ext: 'zip',
        sizeBytes: 1024,
        sha256: 'a'.repeat(64),
        validationStatus: 'INVALID',
        payloadStatus: 'QUARANTINED',
        validationAttempts: 1,
        validationMessage: '结构校验未通过：压缩包层级超过 5',
        createdAt: '2026-10-05T00:01:00Z',
        firstRecipientDownloadAt: null,
        purgeDueAt: null,
        purgedAt: null,
        downloadable: false,
      }],
      approval: null,
    }
    const api = { transfer: vi.fn().mockResolvedValue(detail) }

    renderWithOem(
      <MemoryRouter initialEntries={['/oem/transfers/44']}>
        <Routes><Route path="/oem/transfers/:id" element={<TransferDetailPage />} /></Routes>
      </MemoryRouter>,
      api,
    )

    expect(await screen.findByText('结构校验未通过：压缩包层级超过 5')).toBeInTheDocument()
    expect(screen.getByText('校验未通过')).toBeInTheDocument()
    expect(screen.getByText('暂存中')).toBeInTheDocument()
    expect(screen.queryByText(/病毒|扫描|隔离/)).not.toBeInTheDocument()
  })
})
