import { describe, expect, it } from 'vitest'
import { isQuietRequestError } from '../api/quietRequest'

const failure = (config: object | undefined, status?: number, code?: string) => ({
  config, code, response: status === undefined ? undefined : { status },
})

describe('isQuietRequestError', () => {
  it('quietNetworkError covers transport failures, 404/408/429 and 5xx only', () => {
    const quiet = { quietNetworkError: true }
    for (const status of [404, 408, 429, 500, 503]) expect(isQuietRequestError(failure(quiet, status))).toBe(true)
    expect(isQuietRequestError(failure(quiet, undefined, 'ERR_NETWORK'))).toBe(true)
    expect(isQuietRequestError(failure(quiet, undefined, 'ECONNABORTED'))).toBe(true)
    for (const status of [400, 401, 403, 409, 422]) expect(isQuietRequestError(failure(quiet, status))).toBe(false)
  })

  it('quietClientError covers 400/403/404/413/422 only', () => {
    const quiet = { quietClientError: true }
    for (const status of [400, 403, 404, 413, 422]) expect(isQuietRequestError(failure(quiet, status))).toBe(true)
    for (const status of [401, 409, 429, 500]) expect(isQuietRequestError(failure(quiet, status))).toBe(false)
  })

  it('requests without a quiet flag or without a config still report', () => {
    expect(isQuietRequestError(failure({}, 500))).toBe(false)
    expect(isQuietRequestError(failure(undefined, 500))).toBe(false)
    expect(isQuietRequestError(null)).toBe(false)
  })
})
