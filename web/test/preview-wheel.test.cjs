const test = require('node:test')
const assert = require('node:assert/strict')

test('wheel up enlarges and wheel down reduces at matching reciprocal speeds', async () => {
  const { wheelZoomFactor } = await import('../vendor/preview-wheel.js')
  const up = wheelZoomFactor({ deltaY: -120 }), down = wheelZoomFactor({ deltaY: 120 })
  assert.ok(up > 1 && down < 1)
  assert.ok(Math.abs(up * down - 1) < 1e-10)
  assert.equal(wheelZoomFactor({ deltaY: 100000 }), down)
})

test('pixel, line and page wheel units normalize without intercepting horizontal pan', async () => {
  const { wheelZoomFactor } = await import('../vendor/preview-wheel.js')
  assert.equal(wheelZoomFactor({ deltaY: 16 }), wheelZoomFactor({ deltaY: 1, deltaMode: 1 }))
  assert.equal(wheelZoomFactor({ deltaY: 120 }), wheelZoomFactor({ deltaY: 1, deltaMode: 2 }))
  for (const event of [{ deltaY: 120, shiftKey: true }, { deltaY: 1, deltaX: 20 }, { deltaY: 0 }, { deltaY: NaN }]) {
    assert.equal(wheelZoomFactor(event), 1)
  }
})
