import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import PasswordInput from '../components/PasswordInput'

describe('PasswordInput with Caps Lock detection', () => {
  it('renders password input without caps lock indicator initially', () => {
    render(<PasswordInput placeholder="请输入密码" />)
    const input = screen.getByPlaceholderText('请输入密码')
    expect(input).toBeInTheDocument()
    expect(screen.queryByLabelText('大写锁定已开启')).not.toBeInTheDocument()
  })

  it('shows caps lock indicator when key event has CapsLock modifier', () => {
    render(<PasswordInput placeholder="请输入密码" />)
    const input = screen.getByPlaceholderText('请输入密码')

    // Simulate keydown with CapsLock enabled
    fireEvent.keyDown(input, {
      key: 'A',
      modifierCapsLock: true,
      getModifierState: (key: string) => key === 'CapsLock',
    })

    expect(screen.getByLabelText('大写锁定已开启')).toBeVisible()
    expect(screen.getByText(/大写锁定已开启/)).toBeInTheDocument()

    // Simulate keyup with CapsLock disabled
    fireEvent.keyUp(input, {
      key: 'a',
      modifierCapsLock: false,
      getModifierState: (_key: string) => false,
    })

    expect(screen.queryByLabelText('大写锁定已开启')).not.toBeInTheDocument()
  })

  it('hides caps lock indicator on blur', () => {
    render(<PasswordInput placeholder="请输入密码" />)
    const input = screen.getByPlaceholderText('请输入密码')

    fireEvent.keyDown(input, {
      key: 'A',
      modifierCapsLock: true,
      getModifierState: (key: string) => key === 'CapsLock',
    })
    expect(screen.getByLabelText('大写锁定已开启')).toBeVisible()

    fireEvent.blur(input)
    expect(screen.queryByLabelText('大写锁定已开启')).not.toBeInTheDocument()
  })

  it('toggles password visibility on suffix click', () => {
    render(<PasswordInput placeholder="请输入密码" />)
    const toggle = screen.getByRole('button', { name: '显示密码' })
    const input = screen.getByPlaceholderText('请输入密码')
    expect(input).toHaveAttribute('type', 'password')

    fireEvent.click(toggle)
    expect(screen.getByRole('button', { name: '隐藏密码' })).toBeInTheDocument()
    expect(input).toHaveAttribute('type', 'text')

    fireEvent.click(screen.getByRole('button', { name: '隐藏密码' }))
    expect(input).toHaveAttribute('type', 'password')
  })
})
