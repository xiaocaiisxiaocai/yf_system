import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import PasswordInput from '../../../components/PasswordInput'

function passwordInput() {
  return screen.getByLabelText('密码') as HTMLInputElement
}

describe('password input migration', () => {
  it('toggle click stops the Arco wrapper from moving keyboard focus back to the input', async () => {
    const user = userEvent.setup()
    const wrapperClick = vi.fn()
    render(<div onClick={wrapperClick}><PasswordInput aria-label="密码" /></div>)
    const toggle = screen.getByRole('button', { name: '显示密码' })
    toggle.focus()
    await user.click(toggle)
    expect(wrapperClick).not.toHaveBeenCalled()
    expect(toggle).toHaveFocus()
  })

  it('password toggle is an accessible non-submit button and preserves input props', () => {
    const onChange = vi.fn()
    render(
      <PasswordInput
        aria-label="密码"
        value="secret"
        onChange={onChange}
        prefix={<span>prefix</span>}
        readOnly
        defaultVisibility={false}
      />,
    )
    const input = passwordInput()
    expect(input).toHaveValue('secret')
    expect(input).toHaveAttribute('readonly')
    expect(input).toHaveAttribute('type', 'password')
    expect(screen.getByText('prefix')).toBeVisible()
    const toggle = screen.getByRole('button', { name: '显示密码' })
    expect(toggle).toHaveAttribute('type', 'button')
    expect(toggle).toHaveAttribute('title', '显示密码')
    expect(toggle).toHaveAttribute('aria-pressed', 'false')
    fireEvent.change(input, { target: { value: 'changed' } })
    expect(onChange).toHaveBeenCalled()
  })

  it('clicking the toggle changes visibility and announces the next action', async () => {
    const user = userEvent.setup()
    const onVisibilityChange = vi.fn()
    render(<PasswordInput aria-label="密码" defaultValue="secret" onVisibilityChange={onVisibilityChange} />)
    await user.click(screen.getByRole('button', { name: '显示密码' }))
    expect(passwordInput()).toHaveAttribute('type', 'text')
    expect(screen.getByRole('button', { name: '隐藏密码' })).toHaveAttribute('aria-pressed', 'true')
    expect(onVisibilityChange).toHaveBeenLastCalledWith(true)
    await user.click(screen.getByRole('button', { name: '隐藏密码' }))
    expect(passwordInput()).toHaveAttribute('type', 'password')
    expect(onVisibilityChange).toHaveBeenLastCalledWith(false)
  })

  it('default visibility starts in the requested state', () => {
    render(<PasswordInput aria-label="密码" defaultValue="secret" defaultVisibility />)
    expect(passwordInput()).toHaveAttribute('type', 'text')
    expect(screen.getByRole('button', { name: '隐藏密码' })).toBeVisible()
  })

  it('visibilityToggle=false keeps a custom suffix without adding a toggle', () => {
    render(<PasswordInput aria-label="密码" visibilityToggle={false} suffix={<span>自定义后缀</span>} />)
    expect(screen.getByText('自定义后缀')).toBeVisible()
    expect(screen.queryByRole('button', { name: /密码/ })).not.toBeInTheDocument()
  })

  it('controlled visibility and disabled state keep their existing contracts', async () => {
    const user = userEvent.setup()
    const onVisibilityChange = vi.fn()
    const view = render(
      <PasswordInput aria-label="密码" defaultValue="secret" visibility={false} onVisibilityChange={onVisibilityChange} />,
    )
    await user.click(screen.getByRole('button', { name: '显示密码' }))
    expect(passwordInput()).toHaveAttribute('type', 'password')
    expect(onVisibilityChange).toHaveBeenCalledWith(true)
    view.rerender(
      <PasswordInput aria-label="密码" defaultValue="secret" visibility onVisibilityChange={onVisibilityChange} />,
    )
    expect(passwordInput()).toHaveAttribute('type', 'text')
    view.rerender(
      <PasswordInput aria-label="密码" defaultValue="secret" disabled onVisibilityChange={onVisibilityChange} />,
    )
    const toggle = screen.getByRole('button', { name: '显示密码' })
    expect(toggle).toBeDisabled()
    await user.click(toggle)
    expect(onVisibilityChange).toHaveBeenCalledTimes(1)
  })

  it('toggle does not submit its containing form', async () => {
    const user = userEvent.setup()
    const submit = vi.fn((event: React.FormEvent) => event.preventDefault())
    render(<form onSubmit={submit}><PasswordInput aria-label="密码" /></form>)
    await user.click(screen.getByRole('button', { name: '显示密码' }))
    expect(submit).not.toHaveBeenCalled()
  })
})
