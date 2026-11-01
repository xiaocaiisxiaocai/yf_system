import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Link, MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import UploadNavigationGuard from '../components/UploadNavigationGuard'
import { confirmUploadLeave, UPLOAD_ROUTE_LEAVE_TITLE } from '../components/uploadLeave'
import { retainActiveUpload } from '../components/uploadActivity'

function Probe() {
  const location = useLocation()
  const navigate = useNavigate()
  return <div>
    <output aria-label="当前地址">{`${location.pathname}${location.search}`}</output>
    <Link to="/other">去别处</Link>
    <button type="button" onClick={() => navigate('/pushed')}>程序跳转</button>
    <button type="button" onClick={() => navigate('/?tab=files')}>同页参数</button>
    <button type="button" onClick={() => navigate('/replaced', { replace: true })}>替换跳转</button>
    <button type="button" onClick={() => navigate(-1)}>后退</button>
  </div>
}

function renderGuarded() {
  return render(
    <MemoryRouter initialEntries={['/start', '/']} initialIndex={1}>
      <UploadNavigationGuard>
        <Routes><Route path="*" element={<Probe />} /></Routes>
      </UploadNavigationGuard>
    </MemoryRouter>,
  )
}

const address = () => screen.getByLabelText('当前地址').textContent

describe('站内路由切换的上传确认', () => {
  const releases: Array<() => void> = []

  afterEach(() => {
    while (releases.length > 0) releases.pop()?.()
    document.querySelectorAll('.arco-modal-wrapper, .arco-modal-mask').forEach(node => node.remove())
  })

  it('没有进行中的上传时直接放行', () => {
    renderGuarded()
    fireEvent.click(screen.getByText('去别处'))
    expect(address()).toBe('/other')
    expect(screen.queryByText(UPLOAD_ROUTE_LEAVE_TITLE)).not.toBeInTheDocument()
  })

  it('上传中点链接或程序跳转先确认，取消则留在原页，确认后才放行', async () => {
    renderGuarded()
    releases.push(retainActiveUpload())

    fireEvent.click(screen.getByText('去别处'))
    expect(await screen.findByText(UPLOAD_ROUTE_LEAVE_TITLE)).toBeInTheDocument()
    expect(address()).toBe('/')
    fireEvent.click(screen.getByRole('button', { name: '继续上传' }))
    await waitFor(() => expect(screen.queryByText(UPLOAD_ROUTE_LEAVE_TITLE)).not.toBeInTheDocument())
    expect(address()).toBe('/')

    fireEvent.click(screen.getByText('程序跳转'))
    expect(await screen.findByText(UPLOAD_ROUTE_LEAVE_TITLE)).toBeInTheDocument()
    expect(address()).toBe('/')
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: '离开并中止' })) })
    await waitFor(() => expect(address()).toBe('/pushed'))
  })

  it('对话框打开期间重复触发只留一个确认框', async () => {
    renderGuarded()
    releases.push(retainActiveUpload())

    fireEvent.click(screen.getByText('去别处'))
    fireEvent.click(screen.getByText('程序跳转'))
    await screen.findByText(UPLOAD_ROUTE_LEAVE_TITLE)

    expect(screen.getAllByText(UPLOAD_ROUTE_LEAVE_TITLE)).toHaveLength(1)
    fireEvent.click(screen.getByRole('button', { name: '继续上传' }))
    expect(address()).toBe('/')
  })

  it('同一页面内的参数变化和 replace 重定向不被拦截', () => {
    renderGuarded()
    releases.push(retainActiveUpload())

    fireEvent.click(screen.getByText('同页参数'))
    expect(address()).toBe('/?tab=files')
    fireEvent.click(screen.getByText('替换跳转'))
    expect(address()).toBe('/replaced')
    expect(screen.queryByText(UPLOAD_ROUTE_LEAVE_TITLE)).not.toBeInTheDocument()
  })

  it('上传中后退同样先确认', async () => {
    renderGuarded()
    releases.push(retainActiveUpload())

    fireEvent.click(screen.getByText('后退'))
    expect(await screen.findByText(UPLOAD_ROUTE_LEAVE_TITLE)).toBeInTheDocument()
    expect(address()).toBe('/')
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: '离开并中止' })) })
    await waitFor(() => expect(address()).toBe('/start'))
  })

  it('confirmUploadLeave 空闲时立即执行，上传中确认后才执行，且确认后的连带离开不再重复询问', async () => {
    const idle = vi.fn()
    confirmUploadLeave(idle)
    expect(idle).toHaveBeenCalledOnce()

    releases.push(retainActiveUpload())
    const nested = vi.fn()
    const proceed = vi.fn(() => confirmUploadLeave(nested))
    confirmUploadLeave(proceed)
    expect(proceed).not.toHaveBeenCalled()
    expect(await screen.findByText(UPLOAD_ROUTE_LEAVE_TITLE)).toBeInTheDocument()
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: '离开并中止' })) })

    expect(proceed).toHaveBeenCalledOnce()
    expect(nested).toHaveBeenCalledOnce()
  })
})
