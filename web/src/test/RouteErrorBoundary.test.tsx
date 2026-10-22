import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { documentNavigation } from '../components/documentNavigation'
import RouteErrorBoundary from '../components/RouteErrorBoundary'

function Boom(): JSX.Element {
  throw new Error('boom')
}

function LocationDriver() {
  const navigate = useNavigate()
  return (
    <>
      <button type="button" onClick={() => navigate('/ok')}>改地址</button>
      <RouteErrorBoundary>
        <Routes>
          <Route path="/" element={<Boom />} />
          <Route path="/ok" element={<div>已恢复</div>} />
        </Routes>
      </RouteErrorBoundary>
    </>
  )
}

describe('RouteErrorBoundary', () => {
  beforeEach(() => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the error result and returns to the previous route', async () => {
    const user = userEvent.setup()
    render(
      <MemoryRouter initialEntries={['/a', '/b']} initialIndex={1}>
        <RouteErrorBoundary>
          <Routes>
            <Route path="/a" element={<div>上一页</div>} />
            <Route path="/b" element={<Boom />} />
          </Routes>
        </RouteErrorBoundary>
      </MemoryRouter>,
    )
    expect(screen.getByText('页面出错')).toBeInTheDocument()
    expect(screen.getByText('当前页面出现异常，可重新加载或返回上一页。')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '返回' }))
    expect(await screen.findByText('上一页')).toBeInTheDocument()
    expect(screen.queryByText('页面出错')).not.toBeInTheDocument()
  })

  it('clears the error when the location changes', async () => {
    const user = userEvent.setup()
    render(
      <MemoryRouter initialEntries={['/']}>
        <LocationDriver />
      </MemoryRouter>,
    )
    expect(screen.getByText('页面出错')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '改地址' }))
    expect(await screen.findByText('已恢复')).toBeInTheDocument()
    expect(screen.queryByText('页面出错')).not.toBeInTheDocument()
  })

  it('reloads the page from the error result', async () => {
    const reload = vi.spyOn(documentNavigation, 'reload').mockImplementation(() => undefined)
    const user = userEvent.setup()
    render(
      <MemoryRouter>
        <RouteErrorBoundary>
          <Boom />
        </RouteErrorBoundary>
      </MemoryRouter>,
    )
    await user.click(screen.getByRole('button', { name: '重新加载' }))
    expect(reload).toHaveBeenCalledOnce()
  })
})
