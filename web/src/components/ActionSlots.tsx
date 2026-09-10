import type { ReactNode } from 'react'

export type ActionSlot = ReactNode | false | null | undefined

export type ActionSlotsVariant =
  | 'project'
  | 'supplier'
  | 'account'
  | 'user'
  | 'role'
  | 'file'
  | 'single'

export function actionSlots(slots: ActionSlot[], variant: ActionSlotsVariant) {
  return (
    <div className={`action-slots action-slots--${variant}`}>
      {slots.map((slot, index) => (
        <span
          // Slot order is the alignment contract for same actions across rows.
          key={index}
          className={slot ? 'action-slot' : 'action-slot action-slot--empty'}
          aria-hidden={slot ? undefined : true}
        >
          {slot || null}
        </span>
      ))}
    </div>
  )
}
