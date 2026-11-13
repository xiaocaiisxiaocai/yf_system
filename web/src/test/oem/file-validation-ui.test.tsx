import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { createAppQueryClient } from '../../api/queryClient'
import { OemProvider } from '../../oem/OemContext'
import type { OemApi } from '../../oem/api/OemApi'
import type { SettingItem, TransferDetail } from '../../oem/api/types'
import TransferDetailPage from '../../oem/pages/TransferDetailPage'
import { SettingsHub } from '../../oem/pages/OemHubs'

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

describe('ODM/OEMA 文件校验界面', () => {
  it('设置页显示文件规则，并过滤旧病毒扫描与压缩包上限参数', async () => {
    const settings: SettingItem[] = [
      {
        key: 'oem.validation.max_retries',
        label: '文件校验错误最大重试次数',
        kind: 'integer',
        value: '5',
        min: 0,
        max: 50,
        readOnly: false,
        unsupportedReason: null,
        hint: null,
      },
      {
        key: 'oem.validation.archive_max_depth',
        label: '压缩包嵌套层级上限',
        kind: 'integer',
        value: '5',
        min: 1,
        max: 32,
        readOnly: false,
        unsupportedReason: null,
        hint: null,
      },
      {
        key: 'oem.scan.max_signature_age_hours',
        label: '病毒库最长未更新时间（小时）',
        kind: 'integer',
        value: '48',
        min: 1,
        max: 2160,
        readOnly: false,
        unsupportedReason: null,
        hint: null,
      },
      {
        key: 'oem.scan.block_on_stale_signatures',
        label: '病毒库过期时暂停放行',
        kind: 'boolean',
        value: 'false',
        min: null,
        max: null,
        readOnly: false,
        unsupportedReason: null,
        hint: null,
      },
    ]
    const api = {
      settings: vi.fn().mockResolvedValue(settings),
      updateSettings: vi.fn(),
    }

    renderWithOem(<MemoryRouter><SettingsHub /></MemoryRouter>, api, ['oem:file_policy_manage'])

    expect(await screen.findByLabelText('文件校验错误最大重试次数')).toHaveValue('5')
    expect(screen.getByText('按需设置文件类型、上传容量及下载限制。')).toBeInTheDocument()
    expect(screen.queryByText(/病毒|扫描|压缩包/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '保存更改' })).toBeDisabled()
  })

  it('详情的状态列显示校验失败及服务端返回的原因', async () => {
    const detail: TransferDetail = {
      summary: {
        id: 44,
        direction: 'OEM_TO_INTERNAL',
        companyId: 9,
        companyName: '测试代工厂',
        title: '待处理图纸',
        sender: { realm: 'oem', id: 31, employeeNo: 'OEM31', realName: '厂商用户' }, signerName: null,
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
        downloadableCount: 0,
        previewableCount: 0,
        createdAt: '2026-10-05T00:00:00Z',
        sentAt: null,
        releasedAt: null,
        purgeDueAt: null,
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
        validationMessage: '文件内容与扩展名 .pdf 不符',
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

    expect(await screen.findByText('文件内容与扩展名 .pdf 不符')).toBeInTheDocument()
    expect(screen.getByText('校验未通过')).toBeInTheDocument()
    // 校验与存储合并成一列「状态」，未通过时只显示校验结果。
    expect(screen.getByText('状态')).toBeInTheDocument()
    expect(screen.queryByText('暂存中')).not.toBeInTheDocument()
    expect(screen.queryByText(/病毒|扫描|隔离/)).not.toBeInTheDocument()
  })
})
