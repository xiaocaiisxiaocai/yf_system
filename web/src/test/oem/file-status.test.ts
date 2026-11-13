import { describe, expect, it } from 'vitest'
import { fileStatus } from '../../oem/components/statusLabels'

describe('附件状态合并为一列', () => {
  it('校验未完成或未通过时显示校验结果', () => {
    expect(fileStatus({ validationStatus: 'VALIDATING', payloadStatus: 'QUARANTINED' })).toEqual(['校验中', 'arcoblue'])
    expect(fileStatus({ validationStatus: 'INVALID', payloadStatus: 'QUARANTINED' })).toEqual(['校验未通过', 'red'])
  })

  it('校验通过后显示文件所在阶段', () => {
    expect(fileStatus({ validationStatus: 'VALID', payloadStatus: 'QUARANTINED' })).toEqual(['校验通过', 'green'])
    expect(fileStatus({ validationStatus: 'VALID', payloadStatus: 'PROMOTING' })).toEqual(['入库中', 'arcoblue'])
    expect(fileStatus({ validationStatus: 'VALID', payloadStatus: 'AVAILABLE' })).toEqual(['可用', 'green'])
  })

  it('删除或存储异常优先显示', () => {
    expect(fileStatus({ validationStatus: 'VALID', payloadStatus: 'PURGED' })).toEqual(['已删除', 'gray'])
    expect(fileStatus({ validationStatus: 'INVALID', payloadStatus: 'PURGED' })).toEqual(['已删除', 'gray'])
    expect(fileStatus({ validationStatus: 'VALID', payloadStatus: 'STORAGE_LOST' })).toEqual(['存储丢失', 'red'])
  })
})
