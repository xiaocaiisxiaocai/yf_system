import { describe, expect, it } from 'vitest'
import { wheelZoomFactor } from '../../../../vendor/preview-wheel.js'

describe('preview wheel normalization', () => {
  it('wheel up enlarges and wheel down reduces at matching reciprocal speeds', () => {
    const up = wheelZoomFactor({ deltaY: -120 })
    const down = wheelZoomFactor({ deltaY: 120 })

    expect(up).toBeGreaterThan(1)
    expect(down).toBeLessThan(1)
    expect(up * down).toBeCloseTo(1, 10)
    expect(wheelZoomFactor({ deltaY: 100_000 })).toBe(down)
  })

  it('pixel, line and page wheel units normalize without intercepting horizontal pan', () => {
    expect(wheelZoomFactor({ deltaY: 16 })).toBe(wheelZoomFactor({ deltaY: 1, deltaMode: 1 }))
    expect(wheelZoomFactor({ deltaY: 120 })).toBe(wheelZoomFactor({ deltaY: 1, deltaMode: 2 }))

    for (const event of [
      { deltaY: 120, shiftKey: true },
      { deltaY: 1, deltaX: 20 },
      { deltaY: 0 },
      { deltaY: Number.NaN },
    ]) {
      expect(wheelZoomFactor(event)).toBe(1)
    }
  })
})
