import { useHubSwitcher } from './hubSwitcherContext'

/** Toolbar row for pages that have no toolbar of their own; renders nothing outside a merged page. */
export function HubToolbar() {
  const switcher = useHubSwitcher()
  return switcher ? <div className="page-toolbar responsive-toolbar">{switcher}</div> : null
}
