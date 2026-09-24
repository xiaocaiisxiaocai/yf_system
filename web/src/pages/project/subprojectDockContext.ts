import { createContext, type ReactNode } from 'react'
import type { ProjectSummary } from '../../api/types'

export interface SubprojectDockContextValue {
  /** 主项目接口返回的子项目，面板标题、状态和未读数都以它为准。 */
  projects: ReadonlyMap<number, ProjectSummary>
  /** 主项目页负责的管理操作（编辑、复制、删除），保留原有的防重和幂等逻辑。 */
  renderManageActions: (project: ProjectSummary) => ReactNode
  /** 子项目发生变化后刷新主项目进度和列表。 */
  onGroupChanged: () => void
}

export const SubprojectDockContext = createContext<SubprojectDockContextValue>({
  projects: new Map(),
  renderManageActions: () => null,
  onGroupChanged: () => undefined,
})
