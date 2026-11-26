import { QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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

  it('文件大小上限在界面中以 MB 编辑，保存时按字节提交给服务端', async () => {
    const user = userEvent.setup()
    const settings: SettingItem[] = [
      {
        key: 'oem.upload.max_file_size',
        label: 'ODM/OEMA 单文件大小上限（字节）',
        kind: 'integer',
        value: String(2 * 1024 * 1024 * 1024),
        min: 1024 * 1024,
        max: 10 * 1024 * 1024 * 1024,
        readOnly: false,
        unsupportedReason: null,
        hint: null,
      },
    ]
    const updateSettings = vi.fn().mockResolvedValue(undefined)
    const api = {
      settings: vi.fn().mockResolvedValue(settings),
      updateSettings,
    }

    renderWithOem(<MemoryRouter><SettingsHub /></MemoryRouter>, api, ['oem:file_policy_manage'])

    const input = await screen.findByLabelText('ODM/OEMA 单文件大小上限（MB）')
    expect(input).toHaveValue('2048')
    expect(screen.getByText('约 2.00 GB')).toBeInTheDocument()

    await user.clear(input)
    await user.type(input, '512')
    expect(screen.getByText('约 512.0 MB')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '保存更改' }))

    await waitFor(() => expect(updateSettings).toHaveBeenCalledWith('file', [
      { key: 'oem.upload.max_file_size', value: String(512 * 1024 * 1024) },
    ]))
  })

  it('清空容量输入或输入 - / . 时禁用保存并标记错误，不提交空值或 NaN', async () => {
    const user = userEvent.setup()
    const settings: SettingItem[] = [
      {
        key: 'oem.upload.max_file_size',
        label: 'ODM/OEMA 单文件大小上限（字节）',
        kind: 'integer',
        value: String(2 * 1024 * 1024 * 1024),
        min: 1024 * 1024,
        max: 10 * 1024 * 1024 * 1024,
        readOnly: false,
        unsupportedReason: null,
        hint: null,
      },
      {
        key: 'oem.upload.chunk_size',
        label: '上传分片大小（字节）',
        kind: 'integer',
        value: '1500000',
        min: 1500000,
        max: 64 * 1024 * 1024,
        readOnly: false,
        unsupportedReason: null,
        hint: null,
      },
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
    ]
    const updateSettings = vi.fn().mockResolvedValue(undefined)
    renderWithOem(<MemoryRouter><SettingsHub /></MemoryRouter>, {
      settings: vi.fn().mockResolvedValue(settings),
      updateSettings,
    }, ['oem:file_policy_manage'])

    const fileSizeInput = await screen.findByLabelText('ODM/OEMA 单文件大小上限（MB）')
    const chunkSizeInput = screen.getByLabelText('上传分片大小（MB）')
    const retriesInput = screen.getByLabelText('文件校验错误最大重试次数')
    const saveButton = screen.getByRole('button', { name: '保存更改' })

    // 未修改的非整 MB 字段聚焦后失焦不产生静默漂移。
    fireEvent.focus(chunkSizeInput)
    fireEvent.blur(chunkSizeInput)
    expect(saveButton).toBeDisabled()

    // 清空字节 MB 输入 -> 禁用保存并展示错误，无法提交空字符串。
    await user.clear(fileSizeInput)
    expect(saveButton).toBeDisabled()
    expect(fileSizeInput.closest('.arco-input-inner-wrapper')).toHaveClass('arco-input-inner-wrapper-error')
    expect(screen.getByText('请输入有效数值')).toBeInTheDocument()
    expect(screen.queryByText(/NaN/)).not.toBeInTheDocument()

    // 恢复有效值后，直接输入 '-' 和 '.' 触发 Arco InputNumber 传入 NaN -> 禁用保存且不提交 'NaN'。
    await user.type(fileSizeInput, '256')
    expect(saveButton).toBeEnabled()
    fireEvent.change(fileSizeInput, { target: { value: '-' } })
    expect(saveButton).toBeDisabled()
    expect(fileSizeInput.closest('.arco-input-inner-wrapper')).toHaveClass('arco-input-inner-wrapper-error')
    expect(screen.queryByText(/NaN/)).not.toBeInTheDocument()

    await user.clear(fileSizeInput)
    await user.type(fileSizeInput, '256')
    expect(saveButton).toBeEnabled()
    fireEvent.change(fileSizeInput, { target: { value: '.' } })
    expect(saveButton).toBeDisabled()
    expect(fileSizeInput.closest('.arco-input-inner-wrapper')).toHaveClass('arco-input-inner-wrapper-error')

    // 非字节整型输入强制 precision={0}，小数四舍五入为整数。
    await user.clear(retriesInput)
    await user.type(retriesInput, '7.6')
    fireEvent.blur(retriesInput)
    expect(retriesInput).toHaveValue('8')
    // fileSizeInput 仍无效，因此整体保存按钮仍保持禁用。
    expect(saveButton).toBeDisabled()

    // 修复 fileSizeInput 为合法小数 MB 后可保存，提交精确整数字节与整型重试次数，且未修改的 chunk_size 不被提交。
    await user.clear(fileSizeInput)
    await user.type(fileSizeInput, '1.5')
    fireEvent.blur(fileSizeInput)
    expect(fileSizeInput).toHaveValue('1.5')
    expect(saveButton).toBeEnabled()
    await user.click(saveButton)

    await waitFor(() => expect(updateSettings).toHaveBeenCalledWith('file', [
      { key: 'oem.upload.max_file_size', value: String(Math.round(1.5 * 1024 * 1024)) },
      { key: 'oem.validation.max_retries', value: '8' },
    ]))
  })

  it('无下限的字节设置输入 0 或负数时禁用保存，不允许提交非正字节值', async () => {
    const user = userEvent.setup()
    const settings: SettingItem[] = [
      {
        key: 'oem.upload.max_storage_per_company',
        label: '单厂商存储上限（字节）',
        kind: 'integer',
        value: String(10 * 1024 * 1024),
        min: null,
        max: null,
        readOnly: false,
        unsupportedReason: null,
        hint: null,
      },
    ]
    const updateSettings = vi.fn().mockResolvedValue(undefined)
    renderWithOem(<MemoryRouter><SettingsHub /></MemoryRouter>, {
      settings: vi.fn().mockResolvedValue(settings),
      updateSettings,
    }, ['oem:file_policy_manage'])

    const storageInput = await screen.findByLabelText('单厂商存储上限（MB）')
    const saveButton = screen.getByRole('button', { name: '保存更改' })

    await user.clear(storageInput)
    await user.type(storageInput, '0')
    expect(saveButton).toBeDisabled()
    expect(screen.getByText('数值必须大于 0')).toBeInTheDocument()
    expect(updateSettings).not.toHaveBeenCalled()
  })

  it('详情的状态列显示校验失败及服务端返回的原因', async () => {
    const detail: TransferDetail = {
      summary: {
        id: 44,
        direction: 'OEM_TO_INTERNAL',
        companyId: 9,
        companyName: '测试代工厂',
        title: '待处理图纸',
        sender: { realm: 'oem', id: 31, employeeNo: 'OEM31', realName: '厂商用户' }, signerName: null, recipientNames: [],
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
