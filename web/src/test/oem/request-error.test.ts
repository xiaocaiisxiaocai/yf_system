import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios'
import { describe, expect, it } from 'vitest'
import { oemRequestError } from '../../oem/api/requestError'

const fallback = '操作失败，请重试'

function axiosFailure(message: string, code?: string, data?: unknown): AxiosError {
  const config = { url: '/oem/transfers', headers: {} } as InternalAxiosRequestConfig
  const error = new AxiosError(message, code, config)
  if (data !== undefined) {
    error.response = { status: 500, statusText: '', headers: {}, config, data }
  }
  return error
}

describe('oemRequestError', () => {
  it('uses a non-blank server message and keeps its surrounding spaces', () => {
    const error = axiosFailure('Request failed with status code 400', 'ERR_BAD_REQUEST', { message: ' 厂商已停用 ' })
    expect(oemRequestError(error, fallback)).toBe(' 厂商已停用 ')
    expect(oemRequestError({ response: { data: { message: '策略已被其他管理员修改' } } }, fallback)).toBe('策略已被其他管理员修改')
  })

  it('never surfaces axios english status, cancel, network or timeout text', () => {
    expect(oemRequestError(axiosFailure('Request failed with status code 500', 'ERR_BAD_RESPONSE', {}), fallback)).toBe(fallback)
    expect(oemRequestError(axiosFailure('canceled', 'ERR_CANCELED'), fallback)).toBe(fallback)
    expect(oemRequestError(axiosFailure('Network Error', 'ERR_NETWORK'), fallback)).toBe('网络连接失败，请稍后重试')
    expect(oemRequestError(axiosFailure('timeout of 60000ms exceeded', 'ECONNABORTED'), fallback)).toBe('请求超时，请稍后重试')
    expect(axios.isAxiosError(axiosFailure('canceled', 'ERR_CANCELED'))).toBe(true)
  })

  it('uses a non-axios error message and otherwise the caller fallback', () => {
    expect(oemRequestError(new Error('本地校验失败'), fallback)).toBe('本地校验失败')
    expect(oemRequestError(new Error('   '), fallback)).toBe(fallback)
    expect(oemRequestError({ response: { data: { message: '  ' } } }, fallback)).toBe(fallback)
    expect(oemRequestError({ response: { data: { message: 500 } } }, fallback)).toBe(fallback)
    expect(oemRequestError(null, fallback)).toBe(fallback)
  })
})
