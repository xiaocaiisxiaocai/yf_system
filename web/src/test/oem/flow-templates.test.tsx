import { act, fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AxiosError } from 'axios'
import { describe, expect, it, vi } from 'vitest'
import type { OemApi } from '../../oem/api/OemApi'
import type { FlowTemplate } from '../../oem/api/types'
import { OemProvider } from '../../oem/OemContext'
import FlowTemplatesPage from '../../oem/pages/admin/FlowTemplatesPage'


function person(id: number, realName: string) {
  return { id, employeeNo: `A${id}`, realName, eligible: true }
}

function template(overrides: Partial<FlowTemplate> = {}): FlowTemplate {
  return {
    id: 3,
    name: '默认审批',
    isDefault: true,
    status: 'ACTIVE',
    version: 6,
    createdAt: '2026-10-05T00:00:00Z',
    updatedAt: '2026-10-05T00:00:00Z',
    scopes: [{ id: 11, name: '制造课', kind: 'SECTION', status: 'ACTIVE' }],
    nodes: [{
      sortNo: 1,
      name: '课别主管审批',
      approverSource: 'SECTION_LEADER',
      approvalMode: 'ANY',
      selfPolicy: 'SKIP',
      enabled: true,
      approvers: [person(4, '原审批人')],
      fallbacks: [person(8, '原备用')],
    }],
    ...overrides,
  }
}

function renderPage(api: Partial<OemApi>) {
  return render(
    <OemProvider value={{
      api: {
        departments: vi.fn().mockResolvedValue([]),
        approverOptions: vi.fn().mockResolvedValue([
          { id: 4, employeeNo: 'A4', realName: '原审批人' },
          { id: 8, employeeNo: 'A8', realName: '原备用' },
        ]),
        internalUsers: vi.fn().mockResolvedValue([]),
        ...api,
      } as OemApi,
      realm: 'internal',
      base: '/oem',
      permissions: new Set(['oem:flow_template_manage']),
      userId: 7,
      queryScope: ['flow-test'],
    }}>
      <FlowTemplatesPage />
    </OemProvider>,
  )
}

async function chooseOption(user: ReturnType<typeof userEvent.setup>, current: string, option: string) {
  const dialog = screen.getByRole('dialog')
  await user.click(within(dialog).getByText(current))
  // Arco 选项在下拉动画期间是 pointer-events: none，userEvent 点不到。
  fireEvent.click(await screen.findByRole('option', { name: option }))
}

describe('OEM 审批模板', () => {
  it('新建时非指定人员强制单人审批，且不带备用审批人', async () => {
    const user = userEvent.setup()
    const createFlowTemplate = vi.fn().mockResolvedValue(template())
    renderPage({ flowTemplates: vi.fn().mockResolvedValue([]), createFlowTemplate })
    await user.click(await screen.findByRole('button', { name: '新建模板' }))
    await user.type(screen.getByPlaceholderText('模板名称'), '课别模板')
    await user.click(screen.getByRole('button', { name: '确定' }))

    await vi.waitFor(() => expect(createFlowTemplate).toHaveBeenCalledTimes(1))
    expect(createFlowTemplate.mock.calls[0][0]).toEqual({
      name: '课别模板',
      isDefault: false,
      departmentIds: [],
      nodes: [{
        name: '课别主管审批',
        approverSource: 'SECTION_LEADER',
        approvalMode: 'SINGLE',
        selfPolicy: 'SKIP',
        enabled: true,
        approverUserIds: [],
        fallbackUserIds: [],
      }],
    })
    expect(createFlowTemplate.mock.calls[0][0]).not.toHaveProperty('version')
  })

  it('更新带版本；非指定来源强制单人审批并丢掉备用审批人', async () => {
    const user = userEvent.setup()
    const updateFlowTemplateDefinition = vi.fn().mockResolvedValue(template())
    renderPage({
      flowTemplates: vi.fn().mockResolvedValue([template()]),
      updateFlowTemplateDefinition,
    })

    await user.click(await screen.findByText('默认审批'))
    await user.click(screen.getByRole('button', { name: '确定' }))
    await vi.waitFor(() => expect(updateFlowTemplateDefinition).toHaveBeenCalledTimes(1))
    expect(updateFlowTemplateDefinition.mock.calls[0][0]).toBe(3)
    expect(updateFlowTemplateDefinition.mock.calls[0][1]).toMatchObject({
      name: '默认审批',
      status: 'ACTIVE',
      isDefault: true,
      version: 6,
      departmentIds: [11],
      nodes: [{
        approverSource: 'SECTION_LEADER',
        approvalMode: 'SINGLE',
        selfPolicy: 'SKIP',
        approverUserIds: [],
        fallbackUserIds: [],
      }],
    })
  })

  it('改成指定人员后保留或签，只有备用策略才提交备用审批人', async () => {
    const user = userEvent.setup()
    const designated = template({
      nodes: [{
        sortNo: 1,
        name: '指定审批',
        approverSource: 'SPECIFIED_USERS',
        approvalMode: 'ANY',
        selfPolicy: 'DESIGNATED',
        enabled: true,
        approvers: [person(4, '原审批人')],
        fallbacks: [person(8, '原备用')],
      }],
    })
    const updateFlowTemplateDefinition = vi.fn().mockResolvedValue(template())
    const flowTemplates = vi.fn()
      .mockResolvedValueOnce([template({ nodes: [{ ...template().nodes[0], approvalMode: 'SINGLE', approvers: [], fallbacks: [] }] })])
      .mockResolvedValue([designated])
    renderPage({ flowTemplates, updateFlowTemplateDefinition })

    await user.click(await screen.findByText('默认审批'))
    await chooseOption(user, '课别主管', '指定人员')
    await chooseOption(user, '本节点免审', '改由备用审批人审批')
    expect(within(screen.getByRole('dialog')).getByText('或签（任一人通过）')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '确定' }))
    await vi.waitFor(() => expect(updateFlowTemplateDefinition).toHaveBeenCalledTimes(1))
    expect(updateFlowTemplateDefinition.mock.calls[0][1]).toMatchObject({
      version: 6,
      nodes: [{
        approverSource: 'SPECIFIED_USERS',
        approvalMode: 'ANY',
        selfPolicy: 'DESIGNATED',
        approverUserIds: [],
        fallbackUserIds: [],
      }],
    })

    expect(await screen.findByText(/指定审批/)).toBeVisible()
    await user.click(screen.getByText('默认审批'))
    await user.click(screen.getByRole('button', { name: '确定' }))
    await vi.waitFor(() => expect(updateFlowTemplateDefinition).toHaveBeenCalledTimes(2))
    expect(updateFlowTemplateDefinition.mock.calls[1][1].nodes[0]).toMatchObject({
      approverSource: 'SPECIFIED_USERS',
      approvalMode: 'ANY',
      selfPolicy: 'DESIGNATED',
      approverUserIds: [4],
      fallbackUserIds: [8],
    })
  })

  it('同步双击只创建一次', async () => {
    let resolveCreate!: (value: FlowTemplate) => void
    const createFlowTemplate = vi.fn().mockImplementation(() => new Promise<FlowTemplate>((resolve) => { resolveCreate = resolve }))
    renderPage({ flowTemplates: vi.fn().mockResolvedValue([]), createFlowTemplate })
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: '新建模板' }))
    const ok = screen.getByRole('button', { name: '确定' })
    await act(async () => {
      fireEvent.click(ok)
      fireEvent.click(ok)
    })
    expect(createFlowTemplate).toHaveBeenCalledTimes(1)

    resolveCreate(template({ name: '新建的模板' }))
    expect(await screen.findByText('审批模板已保存，仅影响之后发送的传递单')).toBeInTheDocument()
    expect(createFlowTemplate).toHaveBeenCalledTimes(1)
  })

  it('加载失败显示重试，失败刷新不会清掉已有模板', async () => {
    const user = userEvent.setup()
    const flowTemplates = vi.fn()
      .mockRejectedValueOnce(new AxiosError('Request failed with status code 500', 'ERR_BAD_RESPONSE'))
      .mockResolvedValueOnce([template()])
      .mockRejectedValueOnce(new AxiosError('Request failed with status code 500', 'ERR_BAD_RESPONSE'))
    const updateFlowTemplateDefinition = vi.fn().mockResolvedValue(template())
    renderPage({ flowTemplates, updateFlowTemplateDefinition })

    expect(await screen.findByRole('button', { name: '重试' })).toBeVisible()
    expect(screen.getByText('审批模板加载失败，请重试')).toBeVisible()
    expect(screen.queryByText('暂无数据')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '重试' }))
    expect(await screen.findByText('默认审批')).toBeVisible()
    expect(screen.queryByRole('button', { name: '重试' })).not.toBeInTheDocument()

    await user.click(screen.getByText('默认审批'))
    await user.click(screen.getByRole('button', { name: '确定' }))
    expect(await screen.findByRole('button', { name: '重试' })).toBeVisible()
    expect(screen.getByText('默认审批')).toBeVisible()
  })
})
