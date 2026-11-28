import { createContext } from 'react'

export const imageFormats = 'image/png,image/jpeg,image/gif,image/webp'
export interface ImageTask {
  key: string
  file: File
  percent: number
  error: string
  controller: AbortController
}

interface ImageControls {
  disabled: boolean
  tasks: ReadonlyMap<string, ImageTask>
  load: (id: number, signal: AbortSignal) => Promise<Blob>
  replace: (file: File, pos: number) => void
  retry: (key: string) => void
  remove: (key: string, pos: number) => void
}
export const ImageControlsContext = createContext<ImageControls | null>(null)
