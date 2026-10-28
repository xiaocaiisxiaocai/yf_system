import { createContext, useContext, type ReactNode } from 'react'

/** Section switch of a merged page (see OemHubs); null when the page stands alone. */
export const HubSwitcherContext = createContext<ReactNode>(null)

/** Placed first in a page toolbar so merged pages keep the same single-card layout. */
export function useHubSwitcher(): ReactNode {
  return useContext(HubSwitcherContext)
}
