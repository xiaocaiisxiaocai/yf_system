import { describe, expect, it } from 'vitest'

import {
  relativePart,
  resolveColor,
} from '../../../../vendor/vue-office-pptx/normalize-pptx.js'

const DRAWING_NS = 'http://schemas.openxmlformats.org/drawingml/2006/main'

function drawingElement(xml: string) {
  return new DOMParser().parseFromString(
    xml.replace('<a:', `<a:`).replace(/<a:([\w]+)/, `<a:$1 xmlns:a="${DRAWING_NS}"`),
    'application/xml',
  ).documentElement
}

function hex(color: { channels: number[] }) {
  return Array.from(color.channels, value => Math.round(value * 255).toString(16).padStart(2, '0'))
    .join('')
    .toUpperCase()
}

function requiredColor(color: ReturnType<typeof resolveColor>) {
  if (!color) throw new Error('Expected the DrawingML color to resolve')
  return color
}

describe('PPTX color and relationship normalization migrated from node:test', () => {
  it('theme placeholder gradient applies tint in linear light, not a black or HSL-flat fill', () => {
    const black = drawingElement('<a:srgbClr val="000000"/>')
    const start = resolveColor(drawingElement(
      '<a:schemeClr val="phClr"><a:lumMod val="110000"/><a:satMod val="105000"/><a:tint val="67000"/></a:schemeClr>',
    ), null, null, black)
    const end = resolveColor(drawingElement(
      '<a:schemeClr val="phClr"><a:tint val="81000"/></a:schemeClr>',
    ), null, null, black)

    expect(hex(requiredColor(start))).toBe('9B9B9B')
    expect(hex(requiredColor(end))).toBe('797979')
  })

  it('color map aliases resolve theme system colors and preserve opacity including zero', () => {
    const theme = drawingElement(
      '<a:theme><a:themeElements><a:clrScheme><a:dk1><a:sysClr val="windowText" lastClr="102030"/></a:dk1></a:clrScheme></a:themeElements></a:theme>',
    )
    const value = resolveColor(drawingElement(
      '<a:schemeClr val="tx1"><a:alpha val="0"/></a:schemeClr>',
    ), theme)

    expect(hex(requiredColor(value))).toBe('102030')
    expect(requiredColor(value).alpha).toBe(0)
  })

  it('unknown transforms and unresolved placeholders are left to the original engine', () => {
    expect(resolveColor(drawingElement(
      '<a:srgbClr val="445566"><a:unknownTransform val="90000"/></a:srgbClr>',
    ))).toBeNull()
    expect(resolveColor(drawingElement('<a:schemeClr val="phClr"/>'))).toBeNull()
  })

  it('package relationship resolution follows sibling parts and refuses escaping the ZIP root', () => {
    expect(relativePart('ppt/slides/slide1.xml', '../slideLayouts/slideLayout2.xml'))
      .toBe('ppt/slideLayouts/slideLayout2.xml')
    expect(relativePart('ppt/slideMasters/slideMaster1.xml', '../theme/theme1.xml'))
      .toBe('ppt/theme/theme1.xml')
    expect(relativePart('ppt/slides/slide1.xml', '../../../outside.xml')).toBeNull()
  })
})
