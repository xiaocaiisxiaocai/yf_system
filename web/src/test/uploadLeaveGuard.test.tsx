import { act, render } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import UploadLeaveGuard, { UPLOAD_LEAVE_MESSAGE } from '../components/UploadLeaveGuard'
import { hasActiveUpload, retainActiveUpload } from '../components/uploadActivity'

describe('upload leave guard', () => {
  const releases: Array<() => void> = []

  afterEach(() => {
    while (releases.length > 0) releases.pop()?.()
  })

  it('counts one retain per uploader and ignores a second release', () => {
    const releaseA = retainActiveUpload()
    const releaseB = retainActiveUpload()
    releases.push(releaseA, releaseB)
    expect(hasActiveUpload()).toBe(true)
    releaseA()
    expect(hasActiveUpload()).toBe(true)
    releaseB()
    expect(hasActiveUpload()).toBe(false)
    releaseB()
    expect(hasActiveUpload()).toBe(false)
  })

  it('warns before unload only while an upload is active', async () => {
    render(<UploadLeaveGuard />)
    const idle = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(idle)
    expect(idle.defaultPrevented).toBe(false)

    let release!: () => void
    await act(async () => { release = retainActiveUpload() })
    releases.push(release)
    const leaving = new Event('beforeunload', { cancelable: true })
    const returnValues: unknown[] = []
    Object.defineProperty(leaving, 'returnValue', {
      configurable: true,
      get: () => returnValues.at(-1),
      set: (value) => { returnValues.push(value) },
    })
    window.dispatchEvent(leaving)
    expect(leaving.defaultPrevented).toBe(true)
    expect(returnValues).toContain(UPLOAD_LEAVE_MESSAGE)

    const second = retainActiveUpload()
    releases.push(second)
    await act(async () => { release() })
    const stillLeaving = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(stillLeaving)
    expect(stillLeaving.defaultPrevented).toBe(true)

    await act(async () => { second() })
    const after = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(after)
    expect(after.defaultPrevented).toBe(false)
  })
})
