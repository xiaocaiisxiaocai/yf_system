/* oxlint-disable react/only-export-components -- shared Vitest render/fixture helpers */
import { render } from '@testing-library/react'
import { vi } from 'vitest'
import { messageMocks } from './mockState'

export { messageMocks }

vi.mock('../../../api/client', async () => {
  const state = (await import('./mockState')).messageMocks
  return { default: { get: state.get, post: state.post, delete: state.delete } }
})

vi.mock('../../../store/auth', async () => {
  const state = await import('./mockState')
  return {
    useAuth: state.selectMessageAuthState,
  }
})

import MessagePanel from '../../../components/MessagePanel'

export interface MessageFixture {
  id: number
  projectId: number
  senderId: number
  senderName: string
  senderType: 'INTERNAL' | 'SUPPLIER'
  content: string
  readCount: number
  totalCount: number
  readByMe: boolean
  createdAt: string
  images?: Array<{ id: number; name: string; sizeBytes: number; mimeType: string }>
}

export function messageRow(id: number, overrides: Partial<MessageFixture> = {}): MessageFixture {
  return {
    id,
    projectId: 1,
    senderId: 2,
    senderName: '留言人',
    senderType: 'INTERNAL',
    content: `留言 ${id}`,
    readCount: 0,
    totalCount: 1,
    readByMe: true,
    createdAt: '2026-09-23T00:00:00Z',
    ...overrides,
  }
}

export function messagePage(list: MessageFixture[], total = list.length) {
  return { data: { list, total, page: 1, pageSize: 20 } }
}

export function renderMessagePanel(props: Partial<React.ComponentProps<typeof MessagePanel>> = {}) {
  return render(<MessagePanel projectId={1} projectStatus="IN_PROGRESS" {...props} />)
}

export function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((ok, fail) => {
    resolve = ok
    reject = fail
  })
  return { promise, resolve, reject }
}

export function renderedMessageIds(container: HTMLElement) {
  return [...container.querySelectorAll<HTMLElement>('[data-message-id]')]
    .map(element => Number(element.dataset.messageId))
}

export function setMessagePermissions(...permissions: string[]) {
  messageMocks.permissions.clear()
  permissions.forEach(permission => messageMocks.permissions.add(permission))
}
