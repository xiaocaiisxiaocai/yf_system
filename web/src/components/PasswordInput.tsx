import { forwardRef, useState, type MouseEvent } from 'react'
import { Input } from '@arco-design/web-react'
import { IconEye, IconEyeInvisible } from '@arco-design/web-react/icon'
import type { InputPasswordProps, RefInputType } from '@arco-design/web-react/es/Input/interface'

const PasswordInput = forwardRef<RefInputType, InputPasswordProps>(function PasswordInput({
  defaultVisibility = false,
  visibilityToggle = true,
  visibility,
  onVisibilityChange,
  suffix,
  disabled,
  ...inputProps
}, ref) {
  const [internalVisibility, setInternalVisibility] = useState(defaultVisibility)
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

  const toggle = visibilityToggle ? (
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

  return (
    <Input.Password
      {...inputProps}
      ref={ref}
      disabled={disabled}
      visibility={visible}
      visibilityToggle={false}
      suffix={toggle}
    />
  )
})

PasswordInput.displayName = 'PasswordInput'

export default PasswordInput
