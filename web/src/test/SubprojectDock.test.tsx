import { act, render, screen } from '@testing-library/react'
import { createRef, useEffect } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ProjectSummary } from '../api/types'

const mocks = vi.hoisted(() => ({ mounts: new Map<number, number>() }))

vi.mock('dockview-react', () => import('./dockviewMock'))
vi.mock('../pages/project/SubprojectPane', () => ({
  default: function SubprojectPaneStub({ params }: { params: { projectId: number } }) {
    useEffect(() => {
      mocks.mounts.set(params.projectId, (mocks.mounts.get(params.projectId) ?? 0) + 1)
    }, [params.projectId])
    return <div>pane {params.projectId}</div>
  },
}))

import SubprojectDock, { type SubprojectDockHandle } from '../pages/project/SubprojectDock'
import { layoutStorageKey } from '../pages/project/subprojectDockLayout'
import { dockviewMockState } from './dockviewMock'

const key = layoutStorageKey(7, 3)

function project(id: number): ProjectSummary {
  return { id, name: `子项目 ${id}`, status: 'IN_PROGRESS' } as ProjectSummary
}

function renderDock(projects: ProjectSummary[]) {
  const ref = createRef<SubprojectDockHandle>()
  const context = { projects: new Map(projects.map((item) => [item.id, item])), renderManageActions: () => null, onGroupChanged: () => undefined }
  const ui = (items: ProjectSummary[]) => (
    <MemoryRouter>
      <SubprojectDock ref={ref} groupId={3} userId={7} projects={items} context={context} />
    </MemoryRouter>
  )
  const view = render(ui(projects))
  return { ref, rerender: (items: ProjectSummary[]) => view.rerender(ui(items)) }
}

/** Flushes dockview's buffered layout-change microtasks and the dock's debounced save. */
async function settle() {
  await act(async () => {
    await Promise.resolve()
    await Promise.resolve()
    vi.advanceTimersByTime(400)
  })
}

describe('SubprojectDock incremental layout', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    localStorage.clear()
    mocks.mounts.clear()
    dockviewMockState.reset()
  })
  afterEach(() => vi.useRealTimers())

  it('adds and removes panels in place without re-applying the whole layout or remounting other panes', async () => {
    const { rerender } = renderDock([project(1), project(2)])
    await settle()
    expect(dockviewMockState.fromJSONCalls).toBe(1)
    // The default layout is not written to storage merely because it was applied.
    expect(localStorage.getItem(key)).toBeNull()

    rerender([project(1), project(2), project(3)])
    await settle()
    expect(dockviewMockState.fromJSONCalls).toBe(1)
    expect(dockviewMockState.addPanelCalls.map((call) => call.id)).toEqual(['subproject-3'])
    expect(dockviewMockState.addPanelCalls[0].position?.direction).toBe('right')
    expect(screen.getByText('pane 3')).toBeInTheDocument()

    rerender([project(1), project(2), project(3), project(4)])
    await settle()
    // Three columns are the limit; a fourth subproject joins the least crowded group as a tab.
    expect(dockviewMockState.addPanelCalls[1].position?.direction).toBe('within')

    rerender([project(1), project(3), project(4)])
    await settle()
    expect(dockviewMockState.removedPanels).toEqual(['subproject-2'])
    expect(screen.queryByText('pane 2')).toBeNull()
    expect(dockviewMockState.fromJSONCalls).toBe(1)
    expect(Object.fromEntries(mocks.mounts)).toEqual({ 1: 1, 2: 1, 3: 1, 4: 1 })
    expect(localStorage.getItem(key)).toBeNull()
  })

  it('saves only after a user layout change and keeps the saved set in step with added subprojects', async () => {
    const { rerender } = renderDock([project(1), project(2)])
    await settle()
    act(() => dockviewMockState.api!.simulateUserLayoutChange())
    await settle()
    expect(Object.keys(JSON.parse(localStorage.getItem(key)!).panels)).toEqual(['subproject-1', 'subproject-2'])

    rerender([project(1), project(2), project(3)])
    await settle()
    expect(Object.keys(JSON.parse(localStorage.getItem(key)!).panels)).toEqual(['subproject-1', 'subproject-2', 'subproject-3'])
  })

  it('reset re-applies the default layout and forgets the saved one', async () => {
    const { ref } = renderDock([project(1), project(2)])
    await settle()
    act(() => dockviewMockState.api!.simulateUserLayoutChange())
    await settle()
    expect(localStorage.getItem(key)).not.toBeNull()

    act(() => ref.current!.resetLayout())
    await settle()
    expect(dockviewMockState.fromJSONCalls).toBe(2)
    expect(localStorage.getItem(key)).toBeNull()
  })
})
