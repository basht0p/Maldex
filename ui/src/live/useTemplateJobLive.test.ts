import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { TemplateJobDto } from '../api/types'

const getTemplateJob = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  getTemplateJob: (...a: unknown[]) => getTemplateJob(...a),
}))

import type { HubConnectionLike } from './useTaskLive'
import { useTemplateJobLive } from './useTemplateJobLive'

const dto = { id: 'job-1', state: 'Queued' } as TemplateJobDto

class FlakyHub implements HubConnectionLike {
  joins = 0
  failJoins = 0
  on(_name: string, _cb: (...args: never[]) => void) {}
  onreconnecting() {}
  onreconnected() {}
  onclose() {}
  async start() {}
  async stop() {}
  async invoke() {
    this.joins++
    if (this.joins <= this.failJoins) throw new Error('join failed')
  }
}

beforeEach(() => {
  vi.useFakeTimers()
  getTemplateJob.mockReset()
  getTemplateJob.mockResolvedValue(dto)
})
afterEach(() => vi.useRealTimers())

const settle = () => act(async () => { await vi.advanceTimersByTimeAsync(0) })
const wait = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })

describe('useTemplateJobLive retry', () => {
  it('retries a failed join with backoff (2 s, 5 s, 10 s) and clears the error on success', async () => {
    const hub = new FlakyHub()
    hub.failJoins = 3
    const { result } = renderHook(() => useTemplateJobLive('job-1', () => hub))
    await settle()
    expect(hub.joins).toBe(1)
    expect(result.current.loadError).toBe('join failed')
    await wait(1999); expect(hub.joins).toBe(1)
    await wait(1); expect(hub.joins).toBe(2)
    await wait(4999); expect(hub.joins).toBe(2)
    await wait(1); expect(hub.joins).toBe(3)
    await wait(9999); expect(hub.joins).toBe(3)
    await wait(1); expect(hub.joins).toBe(4)
    expect(result.current.loadError).toBeNull()
    expect(result.current.job).toEqual(dto)
    await wait(60000)
    expect(hub.joins).toBe(4)
  })

  it('retries a failed load, then every 30 s, and stops on unmount', async () => {
    const hub = new FlakyHub()
    getTemplateJob.mockRejectedValue(new Error('load failed'))
    const { result, unmount } = renderHook(() => useTemplateJobLive('job-1', () => hub))
    await settle()
    await wait(2000); await wait(5000); await wait(10000)
    expect(hub.joins).toBe(4)
    await wait(29999); expect(hub.joins).toBe(4)
    await wait(1); expect(hub.joins).toBe(5)
    expect(result.current.loadError).toBe('load failed')
    unmount()
    await wait(120000)
    expect(hub.joins).toBe(5)
  })
})

describe('useTemplateJobLive merge', () => {
  const step = (state: string, n: number) => ({ state, at: `2026-10-24T10:0${n}:00Z`, message: null })

  it('keeps held steps and the requester when a push has none, and takes longer histories', async () => {
    let push!: (d: TemplateJobDto) => void
    class PushHub extends FlakyHub {
      on(name: string, cb: (...args: never[]) => void) { if (name === 'templateJob') push = cb as unknown as typeof push }
    }
    getTemplateJob.mockResolvedValue({ id: 'job-1', state: 'Configuring', requestedByName: 'Ada', steps: [step('Preparing', 1), step('Installing', 2), step('Configuring', 3)] })
    const { result } = renderHook(() => useTemplateJobLive('job-1', () => new PushHub()))
    await settle()
    expect(result.current.job?.steps).toHaveLength(3)
    act(() => push({ id: 'job-1', state: 'Sealing', message: 'm', requestedByName: null } as TemplateJobDto))
    expect(result.current.job?.state).toBe('Sealing')
    expect(result.current.job?.steps).toHaveLength(3)
    expect(result.current.job?.requestedByName).toBe('Ada')
    act(() => push({ id: 'job-1', state: 'Sealing', steps: [step('Preparing', 1)] } as TemplateJobDto))
    expect(result.current.job?.steps).toHaveLength(3)
    act(() => push({ id: 'job-1', state: 'Verifying', steps: [step('a', 1), step('b', 2), step('c', 3), step('d', 4)] } as TemplateJobDto))
    expect(result.current.job?.steps).toHaveLength(4)
  })
})
