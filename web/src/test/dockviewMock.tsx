import { useEffect, useRef, useState, type ComponentType } from 'react'

// jsdom has no layout engine, so the real dockview cannot measure or render its grid. This double
// keeps the public surface the subproject dock uses: onReady -> fromJSON renders every panel with its params,
// addPanel/removePanel mutate the rendered set in place, and layout-change listeners fire in a microtask
// like dockview's buffered onDidLayoutChange.

interface SerializedPanel { id: string; contentComponent: string; params?: Record<string, unknown>; title?: string; group?: string }
interface Props {
  className?: string
  components: Record<string, ComponentType<{ params: unknown; api: unknown; containerApi: unknown }>>
  onReady: (event: { api: unknown }) => void
}
interface AddPanelOptions {
  id: string
  component: string
  title?: string
  params?: Record<string, unknown>
  position?: { referenceGroup?: { id: string } | string; direction?: string }
}
interface SerializedLeaf { type: 'leaf'; data: { id: string; views: string[] } }
interface SerializedBranch { type: 'branch'; data: SerializedNode[] }
type SerializedNode = SerializedLeaf | SerializedBranch

const noop = { dispose: () => undefined }

function panelApi(panel: SerializedPanel) {
  return {
    id: panel.id,
    title: panel.title,
    isVisible: true,
    location: { type: 'grid' },
    onDidVisibilityChange: () => noop,
    setActive: () => undefined,
    setTitle: (title: string) => { panel.title = title },
    isMaximized: () => false,
    maximize: () => undefined,
    exitMaximized: () => undefined,
  }
}

/** Test hooks: the latest api instance and the mutations the component asked for. */
// eslint-disable-next-line react/only-export-components
export const dockviewMockState = {
  api: undefined as undefined | { simulateUserLayoutChange: () => void; panels: { id: string }[] },
  fromJSONCalls: 0,
  addPanelCalls: [] as AddPanelOptions[],
  removedPanels: [] as string[],
  reset() {
    this.api = undefined
    this.fromJSONCalls = 0
    this.addPanelCalls = []
    this.removedPanels = []
  },
}

function leafGroups(node: SerializedNode | undefined, result = new Map<string, string>()) {
  if (!node) return result
  if (node.type === 'leaf') node.data.views.forEach((view) => result.set(view, node.data.id))
  else node.data.forEach((child) => leafGroups(child, result))
  return result
}

// eslint-disable-next-line react/only-export-components
export const themeLight = { name: 'light', className: 'dockview-theme-light' }

export function DockviewReact({ className, components, onReady }: Props) {
  const [panels, setPanels] = useState<SerializedPanel[]>([])
  const readyRef = useRef(false)
  const [containerApi, setContainerApi] = useState<unknown>(null)

  useEffect(() => {
    if (readyRef.current) return
    readyRef.current = true
    let current: SerializedPanel[] = []
    let nextGroup = 0
    const listeners = new Set<() => void>()
    let queued = false
    const fireLayoutChange = () => {
      if (queued) return
      queued = true
      queueMicrotask(() => { queued = false; listeners.forEach((listener) => listener()) })
    }
    const commit = (next: SerializedPanel[]) => { current = next; setPanels(next); fireLayoutChange() }
    const view = (panel: SerializedPanel) => ({
      id: panel.id, title: panel.title, api: { ...panelApi(panel), close: () => api.removePanel({ id: panel.id }) },
    })
    const api = {
      width: 1200,
      height: 640,
      get panels() { return current.map(view) },
      get groups() {
        const ids = [...new Set(current.map((panel) => panel.group ?? 'group-0'))]
        return ids.map((id) => ({
          id,
          api: { location: { type: 'grid' } },
          panels: current.filter((panel) => (panel.group ?? 'group-0') === id).map(view),
        }))
      },
      fromJSON: (layout: { panels: Record<string, SerializedPanel>; grid?: { root?: SerializedNode } }) => {
        dockviewMockState.fromJSONCalls += 1
        const groups = leafGroups(layout.grid?.root)
        commit(Object.values(layout.panels).map((panel) => ({ ...panel, group: groups.get(panel.id) ?? 'group-0' })))
      },
      toJSON: () => ({ panels: Object.fromEntries(current.map((panel) => [panel.id, panel])) }),
      clear: () => commit([]),
      getPanel: (id: string) => {
        const panel = current.find((item) => item.id === id)
        return panel ? view(panel) : undefined
      },
      addPanel: (options: AddPanelOptions) => {
        dockviewMockState.addPanelCalls.push(options)
        const reference = options.position?.referenceGroup
        const referenceId = typeof reference === 'string' ? reference : reference?.id
        const group = options.position?.direction === 'within' && referenceId ? referenceId : `mock-group-${++nextGroup}`
        commit([...current, {
          id: options.id, contentComponent: options.component, title: options.title, params: options.params, group,
        }])
      },
      removePanel: (panel: { id: string }) => {
        dockviewMockState.removedPanels.push(panel.id)
        commit(current.filter((item) => item.id !== panel.id))
      },
      onDidLayoutChange: (listener: () => void) => {
        listeners.add(listener)
        return { dispose: () => listeners.delete(listener) }
      },
      onDidMaximizedGroupChange: () => noop,
      hasMaximizedGroup: () => false,
      exitMaximizedGroup: () => undefined,
      /** Test-only: simulates a user drag/resize, which dockview reports through onDidLayoutChange. */
      simulateUserLayoutChange: fireLayoutChange,
    }
    dockviewMockState.api = api
    setContainerApi(api)
    onReady({ api })
  }, [onReady])

  return (
    <div className={className} data-testid="dockview">
      {panels.map((panel) => {
        const Component = components[panel.contentComponent]
        return (
          <section key={panel.id} aria-label={panel.title}>
            <Component params={panel.params ?? {}} api={panelApi(panel)} containerApi={containerApi} />
          </section>
        )
      })}
    </div>
  )
}
