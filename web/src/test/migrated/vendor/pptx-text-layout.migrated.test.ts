import JSZip from 'jszip'
import rendererSource from 'pptx-preview/dist/pptx-preview.es.js?raw'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { createServer, type ViteDevServer } from 'vite'

import { patchPptxRenderer } from '../../../../vendor/vue-office-pptx/patch-renderer.mjs'

type RendererModule = {
  init: (host: HTMLElement, options: { width: number; height: number }) => {
    preview: (file: ArrayBuffer) => Promise<unknown>
  }
}

const relationshipNamespace = 'http://schemas.openxmlformats.org/package/2006/relationships'
const officeRelationships = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
const presentationNamespace = 'http://schemas.openxmlformats.org/presentationml/2006/main'
const drawingNamespace = 'http://schemas.openxmlformats.org/drawingml/2006/main'

function relationships(entries: Array<{ id: string; type: string; target: string }>) {
  return `<Relationships xmlns="${relationshipNamespace}">${entries.map(entry =>
    `<Relationship Id="${entry.id}" Type="${officeRelationships}/${entry.type}" Target="${entry.target}"/>`,
  ).join('')}</Relationships>`
}

function shape(id: number, bodyProperties: string, paragraphs: string, geometry: {
  x: number
  y: number
  width: number
  height: number
}) {
  return `<p:sp>
    <p:nvSpPr><p:cNvPr id="${id}" name="Text ${id}"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
    <p:spPr>
      <a:xfrm><a:off x="${geometry.x}" y="${geometry.y}"/><a:ext cx="${geometry.width}" cy="${geometry.height}"/></a:xfrm>
      <a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/>
    </p:spPr>
    <p:txBody><a:bodyPr ${bodyProperties}/><a:lstStyle/>${paragraphs}</p:txBody>
  </p:sp>`
}

function paragraph(text: string, properties = '') {
  return `<a:p><a:pPr>${properties}</a:pPr><a:r><a:rPr sz="3600"><a:ea typeface="宋体"/></a:rPr><a:t>${text}</a:t></a:r><a:endParaRPr/></a:p>`
}

async function textLayoutFixture() {
  const zip = new JSZip()
  zip.file('[Content_Types].xml', `<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
    <Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>
    <Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/>
    <Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>
    <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
  </Types>`)
  zip.file('ppt/presentation.xml', `<p:presentation xmlns:p="${presentationNamespace}" xmlns:a="${drawingNamespace}">
    <p:sldSz cx="12192000" cy="6858000"/><p:defaultTextStyle/>
  </p:presentation>`)
  zip.file('ppt/theme/theme1.xml', `<a:theme xmlns:a="${drawingNamespace}" name="Fixture">
    <a:themeElements>
      <a:clrScheme name="Fixture"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1></a:clrScheme>
      <a:fontScheme name="Fixture"><a:majorFont><a:latin typeface="Arial"/><a:ea typeface="宋体"/></a:majorFont><a:minorFont><a:latin typeface="Arial"/><a:ea typeface="宋体"/></a:minorFont></a:fontScheme>
      <a:fmtScheme name="Fixture"><a:fillStyleLst/><a:lnStyleLst/><a:effectStyleLst/><a:bgFillStyleLst/></a:fmtScheme>
    </a:themeElements>
  </a:theme>`)
  zip.file('ppt/slideMasters/slideMaster1.xml', `<p:sldMaster xmlns:p="${presentationNamespace}" xmlns:a="${drawingNamespace}">
    <p:cSld><p:spTree/></p:cSld><p:clrMap bg1="lt1" tx1="dk1"/><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles>
  </p:sldMaster>`)
  zip.file('ppt/slideMasters/_rels/slideMaster1.xml.rels', relationships([
    { id: 'rId1', type: 'theme', target: '../theme/theme1.xml' },
  ]))
  zip.file('ppt/slideLayouts/slideLayout1.xml', `<p:sldLayout xmlns:p="${presentationNamespace}" xmlns:a="${drawingNamespace}"><p:cSld><p:spTree/></p:cSld></p:sldLayout>`)
  zip.file('ppt/slideLayouts/_rels/slideLayout1.xml.rels', relationships([
    { id: 'rId1', type: 'slideMaster', target: '../slideMasters/slideMaster1.xml' },
  ]))

  const defaultShape = shape(
    2,
    '',
    [
      paragraph('默认行距'),
      paragraph('显式段距', '<a:spcBef><a:spcPts val="240"/></a:spcBef><a:spcAft><a:spcPts val="360"/></a:spcAft>'),
      paragraph('末段'),
    ].join(''),
    { x: 2735857, y: 943804, width: 2675262, height: 646331 },
  )
  const customShape = shape(
    3,
    'lIns="0" rIns="15875" tIns="34925" bIns="0"',
    paragraph('显式行距', '<a:lnSpc><a:spcPct val="150000"/></a:lnSpc>'),
    { x: 0, y: 0, width: 2675262, height: 646331 },
  )
  zip.file('ppt/slides/slide1.xml', `<p:sld xmlns:p="${presentationNamespace}" xmlns:a="${drawingNamespace}">
    <p:cSld><p:spTree>${defaultShape}${customShape}</p:spTree></p:cSld>
  </p:sld>`)
  zip.file('ppt/slides/_rels/slide1.xml.rels', relationships([
    { id: 'rId1', type: 'slideLayout', target: '../slideLayouts/slideLayout1.xml' },
  ]))
  return zip.generateAsync({ type: 'arraybuffer' })
}

describe('patched PPTX renderer text layout migrated from node:test', () => {
  let server: ViteDevServer
  let renderer: RendererModule
  let patchedDependency = false

  beforeAll(async () => {
    server = await createServer({
      root: process.cwd(),
      configFile: false,
      appType: 'custom',
      server: { middlewareMode: true },
      plugins: [{
        name: 'pptx-drawingml-text-layout-test',
        enforce: 'pre',
        transform(code, id) {
          if (!id.replaceAll('\\', '/').endsWith('/pptx-preview/dist/pptx-preview.es.js')) return null
          patchedDependency = true
          const patched = patchPptxRenderer(code)
          const compatible = patched.replace(
            /import\{get as ([\w$]+),omit as ([\w$]+)\}from"lodash";/,
            (_statement: string, getName: string, omitName: string) =>
              `import lodash from "lodash";const{get:${getName},omit:${omitName}}=lodash;`,
          )
          if (compatible === patched) throw new Error('PPTX lodash import anchor changed')
          return { code: compatible, map: null }
        },
      }],
      ssr: { noExternal: ['pptx-preview'] },
    })
    renderer = await server.ssrLoadModule('/node_modules/pptx-preview/dist/pptx-preview.es.js') as RendererModule
    expect(patchedDependency).toBe(true)
  })

  afterAll(async () => {
    await server?.close()
  })

  async function renderFixture() {
    const host = document.createElement('main')
    document.body.append(host)
    const preview = renderer.init(host, { width: 960, height: 540 })
    await preview.preview(await textLayoutFixture())
    return host
  }

  it('paragraph layout has no invented top margin while preserving explicit fractional spacing', async () => {
    const host = await renderFixture()
    const explicit = Array.from(host.querySelectorAll('.text-wrapper > div'))
      .find(element => element.textContent === '显式段距') as HTMLElement
    const defaultLine = Array.from(host.querySelectorAll('.text-wrapper p'))
      .find(element => element.textContent === '默认行距') as HTMLElement
    const explicitLine = Array.from(host.querySelectorAll('.text-wrapper p'))
      .find(element => element.textContent === '显式行距') as HTMLElement

    expect(explicit.style.margin).toBe('0px')
    expect(explicit.style.paddingTop).toBe('2.4px')
    expect(explicit.style.paddingRight).toBe('0px')
    expect(explicit.style.paddingBottom).toBe('3.6px')
    expect(explicit.style.paddingLeft).toBe('0px')
    expect(defaultLine.style.lineHeight).toBe('1.2')
    expect(explicitLine.style.lineHeight).toBe('1.5')
  })

  it('text boxes honor DrawingML default and explicit fractional/zero insets', async () => {
    const host = await renderFixture()
    const wrappers = Array.from(host.querySelectorAll('.shape-wrapper')) as HTMLElement[]
    const defaultText = wrappers.find(wrapper => wrapper.textContent?.includes('默认行距'))!
      .querySelector('.text-wrapper') as HTMLElement
    const customText = wrappers.find(wrapper => wrapper.textContent?.includes('显式行距'))!
      .querySelector('.text-wrapper') as HTMLElement

    expect([
      defaultText.style.paddingTop,
      defaultText.style.paddingRight,
      defaultText.style.paddingBottom,
      defaultText.style.paddingLeft,
    ]).toEqual(['3.6px', '7.2px', '3.6px', '7.2px'])
    expect([
      customText.style.paddingTop,
      customText.style.paddingRight,
      customText.style.paddingBottom,
      customText.style.paddingLeft,
    ]).toEqual(['2.75px', '1.25px', '0px', '0px'])
  })

  it('shape positions and sizes retain sub-point precision before browser zoom', async () => {
    const host = await renderFixture()
    const shapeElement = Array.from(host.querySelectorAll('.shape-wrapper'))
      .find(wrapper => wrapper.textContent?.includes('默认行距')) as HTMLElement
    const actual = {
      x: Number.parseFloat(shapeElement.style.left),
      y: Number.parseFloat(shapeElement.style.top),
      width: Number.parseFloat(shapeElement.style.width),
      height: Number.parseFloat(shapeElement.style.height),
    }

    expect(actual.x).toBeCloseTo(2735857 / 12700, 3)
    expect(actual.y).toBeCloseTo(943804 / 12700, 3)
    expect(actual.width).toBeCloseTo(2675262 / 12700, 3)
    expect(actual.height).toBeCloseTo(646331 / 12700, 3)
    expect(actual.x).not.toBe(Math.round(actual.x))
    expect(actual.width).not.toBe(Math.round(actual.width))
  })

  it('dependency changes cannot silently omit the renderer layout fixes', () => {
    expect(() => patchPptxRenderer(rendererSource.replace('Math.floor(.2*u())', '0')))
      .toThrow(/anchor changed/)
  })
})
