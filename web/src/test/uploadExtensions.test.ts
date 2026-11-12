import { describe, expect, it } from 'vitest'
import { extensionOf, splitSupported, unsupportedMessage } from '../utils/uploadExtensions'

const file = (name: string) => new File(['x'], name)

describe('上传格式预检', () => {
  it('按白名单拒绝不支持的格式，大小写不敏感，无扩展名也拒绝', () => {
    const { accepted, rejected } = splitSupported([file('a.PDF'), file('b.exe'), file('c'), file('d.step')], ['pdf', 'step'])
    expect(accepted.map((item) => item.name)).toEqual(['a.PDF', 'd.step'])
    expect(rejected.map((item) => item.name)).toEqual(['b.exe', 'c'])
    expect(extensionOf('x.tar.GZ')).toBe('gz')
  })

  it('白名单未知或为空时不拦截，交给服务端判断', () => {
    expect(splitSupported([file('a.exe')], null).rejected).toHaveLength(0)
    expect(splitSupported([file('a.exe')], []).rejected).toHaveLength(0)
  })

  it('提示最多列出三个文件名', () => {
    expect(unsupportedMessage([file('1.x'), file('2.x'), file('3.x'), file('4.x')])).toContain('等 4 个文件')
  })
})
