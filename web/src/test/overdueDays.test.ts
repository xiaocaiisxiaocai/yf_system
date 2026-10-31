import { describe, expect, it } from 'vitest'
import { overdueDays } from '../api/types'

describe('overdueDays', () => {
  const now = new Date(2026, 9, 31, 9, 0)

  it('counts whole local days past the due date for unfinished projects only', () => {
    expect(overdueDays('2026-10-28', 'IN_PROGRESS', now)).toBe(3)
    expect(overdueDays('2026-10-30', 'DRAFT', now)).toBe(1)
    expect(overdueDays('2026-10-31', 'IN_PROGRESS', now)).toBeNull()
    expect(overdueDays('2026-11-02', 'IN_PROGRESS', now)).toBeNull()
    expect(overdueDays('2026-10-01', 'COMPLETED', now)).toBeNull()
    expect(overdueDays('2026-10-01', 'TERMINATED', now)).toBeNull()
    expect(overdueDays(null, 'IN_PROGRESS', now)).toBeNull()
    expect(overdueDays('31/10/2026', 'IN_PROGRESS', now)).toBeNull()
  })
})
