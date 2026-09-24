import { useEffect, useRef, useState, type ComponentType } from 'react'

// jsdom has no layout engine, so the real dockview cannot measure or render its grid. This double
// keeps the public surface the subproject dock uses: onReady -> fromJSON renders every panel with its params.

interface SerializedPanel { id: string; contentComponent: string; params?: Record<string, unknown>; title?: string }
interface Props {
  className?: string
  components: Record<string, ComponentType<{ params: unknown; api: unknown; containerApi: unknown }>>
  onReady: (event: { api: unknown }) => void
}

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
    const api = {
      width: 1200,
      height: 640,
      get panels() { return current.map((panel) => ({ id: panel.id, title: panel.title, api: panelApi(panel) })) },
      get groups() { return current.length ? [{}] : [] },
      fromJSON: (layout: { panels: Record<string, SerializedPanel> }) => {
        current = Object.values(layout.panels).map((panel) => ({ ...panel }))
        setPanels(current)
      },
      toJSON: () => ({ panels: Object.fromEntries(current.map((panel) => [panel.id, panel])) }),
      clear: () => { current = []; setPanels([]) },
      getPanel: (id: string) => {
        const panel = current.find((item) => item.id === id)
        return panel ? { id: panel.id, title: panel.title, api: panelApi(panel) } : undefined
      },
      onDidLayoutChange: () => noop,
      onDidMaximizedGroupChange: () => noop,
      hasMaximizedGroup: () => false,
      exitMaximizedGroup: () => undefined,
    }
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
