// These build-time fixes target the pinned 0.0.19 renderer. Fail closed when
// upgrading the dependency rather than silently dropping layout corrections.
export function patchPptxRenderer(source) {
  const replacements = [
    ['f.style.margin="".concat(Math.floor(.2*u()),"px  0 0 0")', 'f.style.margin="0"'],
    ['f.style.padding="".concat(Math.floor(w),"px 0px ").concat(Math.floor(v),"px 0px")', 'f.style.padding="".concat(w,"px 0px ").concat(v,"px 0px")'],
    ['var b=p.hasOwnProperty("lineHeight")?p.lineHeight:1;', 'var b=p.hasOwnProperty("lineHeight")?p.lineHeight:1.2;'],
    ['s.hasOwnProperty("tIns")?Math.floor(s.tIns)+"px":"3px"', 's.hasOwnProperty("tIns")?s.tIns+"px":"3.6px"'],
    ['s.hasOwnProperty("rIns")?Math.floor(s.rIns)+"px":"5px"', 's.hasOwnProperty("rIns")?s.rIns+"px":"7.2px"'],
    ['s.hasOwnProperty("bIns")?Math.floor(s.bIns)+"px":"3px"', 's.hasOwnProperty("bIns")?s.bIns+"px":"3.6px"'],
    ['s.hasOwnProperty("lIns")?Math.floor(s.lIns)+"px":"5px"', 's.hasOwnProperty("lIns")?s.lIns+"px":"7.2px"'],
  ]
  let code = source
  for (const [before, after] of replacements) {
    if (code.split(before).length !== 2) throw new Error(`PPTX renderer layout anchor changed: ${before}`)
    code = code.replace(before, after)
  }
  let coordinates = 0
  code = code.replace(/Math\.round\((p\(parseInt\(o\["a:(?:off|ext)"\]\.attrs\.(?:x|y|cx|cy)\)\))\)/g,
    (_, precise) => { coordinates++; return precise })
  if (coordinates !== 8) throw new Error('PPTX renderer coordinate layout changed')
  return code
}
