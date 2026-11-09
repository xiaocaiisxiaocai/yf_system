import { afterEach, describe, expect, it, vi } from 'vitest'
import { overdueDays } from '../api/types'

describe('overdueDays', () => {
  // 2026-10-31 09:00 +08:00
  const now = new Date('2026-10-31T01:00:00Z')

  afterEach(() => {
    vi.useRealTimers()
  })

  it('counts whole business days past the due date for unfinished projects only', () => {
    expect(overdueDays('2026-10-28', 'IN_PROGRESS', now)).toBe(3)
    expect(overdueDays('2026-10-30', 'IN_PROGRESS', now)).toBe(1)
    expect(overdueDays('2026-10-31', 'IN_PROGRESS', now)).toBeNull()
    expect(overdueDays('2026-11-02', 'IN_PROGRESS', now)).toBeNull()
    expect(overdueDays('2026-10-01', 'COMPLETED', now)).toBeNull()
    expect(overdueDays('2026-10-01', 'TERMINATED', now)).toBeNull()
    expect(overdueDays(null, 'IN_PROGRESS', now)).toBeNull()
    expect(overdueDays('31/10/2026', 'IN_PROGRESS', now)).toBeNull()
  })

  it('rolls the day over at 00:00 UTC+8 regardless of the browser time zone', () => {
    // 2026-10-31 23:59:59 +08:00 仍是 10-31；一秒后进入 11-01。
    expect(overdueDays('2026-10-31', 'IN_PROGRESS', new Date('2026-10-31T15:59:59Z'))).toBeNull()
    expect(overdueDays('2026-10-31', 'IN_PROGRESS', new Date('2026-10-31T16:00:00Z'))).toBe(1)
    // UTC 仍是 10-30 16:30，业务日已是 10-31 00:30：10-30 到期已逾期 1 天。
    expect(overdueDays('2026-10-30', 'IN_PROGRESS', new Date('2026-10-30T16:30:00Z'))).toBe(1)
    expect(overdueDays('2026-10-31', 'IN_PROGRESS', new Date('2026-10-30T16:30:00Z'))).toBeNull()
    // 西半球凌晨（本地时间仍是前一天）也按业务日计算。
    expect(overdueDays('2026-10-30', 'IN_PROGRESS', new Date('2026-10-30T20:00:00-08:00'))).toBe(1)
  })

  it('uses the system clock when no instant is passed', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-10-31T16:00:00Z'))
    expect(overdueDays('2026-10-31', 'IN_PROGRESS')).toBe(1)
    vi.setSystemTime(new Date('2026-10-31T15:59:59Z'))
    expect(overdueDays('2026-10-31', 'IN_PROGRESS')).toBeNull()
  })
})
