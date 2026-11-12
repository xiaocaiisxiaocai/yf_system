import { forwardRef, useState, type FocusEvent, type KeyboardEvent, type MouseEvent } from 'react'
import { Input, Tooltip } from '@arco-design/web-react'
import { IconArrowUp, IconEye, IconEyeInvisible } from '@arco-design/web-react/icon'
import type { InputPasswordProps, RefInputType } from '@arco-design/web-react/es/Input/interface'

const PasswordInput = forwardRef<RefInputType, InputPasswordProps>(function PasswordInput({
  defaultVisibility = false,
  visibilityToggle = true,
  visibility,
  onVisibilityChange,
  suffix,
  disabled,
  onKeyDown,
  onKeyUp,
  onBlur,
  ...inputProps
}, ref) {
  const [internalVisibility, setInternalVisibility] = useState(defaultVisibility)
  const [capsLock, setCapsLock] = useState(false)
  const isControlled = visibility !== undefined
  const visible = isControlled ? visibility : internalVisibility

  const toggleVisibility = (event: MouseEvent<HTMLButtonElement>) => {
    event.preventDefault()
    // Arco focuses the input on bubbling suffix clicks; keep keyboard focus on this button.
    event.stopPropagation()
    if (disabled) return

    const nextVisibility = !visible
    if (!isControlled) setInternalVisibility(nextVisibility)
    onVisibilityChange?.(nextVisibility)
  }

  const handleKeyEvent = (event: KeyboardEvent<HTMLInputElement>, originalHandler?: (e: KeyboardEvent<HTMLInputElement>) => void) => {
    if (typeof event.getModifierState === 'function') {
      setCapsLock(event.getModifierState('CapsLock'))
    }
    originalHandler?.(event)
  }

  const handleBlur = (event: FocusEvent<HTMLInputElement>) => {
    setCapsLock(false)
    onBlur?.(event)
  }

  const capsLockIndicator = capsLock ? (
    <span
      className="password-capslock-badge"
      title="大写锁定已开启"
      aria-label="大写锁定已开启"
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        color: 'rgb(var(--warning-6))',
        marginRight: 4,
      }}
    >
      <IconArrowUp style={{ strokeWidth: 4 }} />
    </span>
  ) : null

  const toggleButton = visibilityToggle ? (
    <button
      type="button"
      className="password-toggle"
      aria-label={visible ? '隐藏密码' : '显示密码'}
      aria-pressed={visible}
      title={visible ? '隐藏密码' : '显示密码'}
      disabled={disabled}
      onClick={toggleVisibility}
      onMouseDown={(event) => event.preventDefault()}
    >
      {suffix || (visible ? <IconEye aria-hidden="true" /> : <IconEyeInvisible aria-hidden="true" />)}
    </button>
  ) : suffix

  const combinedSuffix = (capsLockIndicator || toggleButton) ? (
    <span style={{ display: 'inline-flex', alignItems: 'center' }}>
      {capsLockIndicator}
      {toggleButton}
    </span>
  ) : null

  return (
    <Tooltip content="大写锁定已开启 (Caps Lock)" position="top" trigger="hover" popupVisible={capsLock}>
      <Input.Password
        {...inputProps}
        ref={ref}
        disabled={disabled}
        visibility={visible}
        visibilityToggle={false}
        suffix={combinedSuffix}
        onKeyDown={(e) => handleKeyEvent(e, onKeyDown)}
        onKeyUp={(e) => handleKeyEvent(e, onKeyUp)}
        onBlur={handleBlur}
      />
    </Tooltip>
  )
})

PasswordInput.displayName = 'PasswordInput'

export default PasswordInput
