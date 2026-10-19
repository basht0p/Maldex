import { act, renderHook, waitFor } from '@testing-library/react'
import { StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { LiveEventDto, ProcessNodeDto, TaskStatusUpdate } from '../api/types'

const getProcesses = vi.fn()
const getEvents = vi.fn()
vi.mock('../api/client', () => ({ getProcesses: (...a: unknown[]) => getProcesses(...a), getEvents: (...a: unknown[]) => getEvents(...a) }))

import { type HubConnectionLike, useTaskLive } from './useTaskLive'

class MockHub implements HubConnectionLike {
  handlers = new Map<string, (...args: never[]) => void>()
  reconnecting: Array<() => void> = []
  reconnected: Array<() => void> = []
  closed: Array<() => void> = []
  calls: string[] = []
  started = false
  startCount = 0
  stopped = false
  onJoin?: () => void
  on(name: string, cb: (...args: never[]) => void) { this.handlers.set(name, cb) }
  onreconnecting(cb: () => void) { this.reconnecting.push(cb) }
  onreconnected(cb: () => void) { this.reconnected.push(cb) }
  onclose(cb: () => void) { this.closed.push(cb) }
  async start() { this.started = true; this.startCount++; this.calls.push('start') }
  async stop() { this.stopped = true; this.calls.push('stop') }
  async invoke(method: string, ...args: unknown[]) {
    this.calls.push(`${method}:${args.join(',')}`)
    if (method === 'JoinTask') this.onJoin?.()
  }
  push(name: string, payload: unknown) { act(() => { (this.handlers.get(name) as (p: unknown) => void)(payload) }) }
}

const ev = (id: number): LiveEventDto => ({ id, type: 'File', at: '2026-10-19T00:00:00Z', pid: 1, processId: 1, summary: `e${id}`, dataJson: '{}' })
const proc = (id: number, over: Partial<ProcessNodeDto> = {}): ProcessNodeDto => ({
  id, pid: id * 10, parentId: null, imagePath: `p${id}.exe`, commandLine: `p${id}.exe`, startedAt: '2026-10-19T00:00:00Z', ...over,
})
const status = (s: TaskStatusUpdate['status']): TaskStatusUpdate => ({ taskId: 't1', status: s, verdict: 'Unknown', deadlineAt: null })
const ids = (xs: Array<{ id: number }>) => xs.map((x) => x.id)

beforeEach(() => {
  getProcesses.mockReset()
  getEvents.mockReset()
})
afterEach(() => vi.useRealTimers())

describe('useTaskLive', () => {
  it('useTaskLive_MergesBacklogAndLive_NoDuplicates', async () => {
    const hub = new MockHub()
    getProcesses.mockResolvedValue([proc(1), proc(2)])
    // An event arrives live between JoinTask and the REST fetch: it is in the buffer AND the backlog (ids 3 and 4).
    hub.onJoin = () => {
      hub.push('events', [ev(3), ev(4)])
      hub.push('process', proc(2, { endedAt: '2026-10-19T00:01:00Z' }))
      hub.push('status', status('Running'))
    }
    getEvents.mockResolvedValue([ev(1), ev(2), ev(3), ev(4)])

    const { result } = renderHook(() => useTaskLive('t1', { createConnection: () => hub }))
    await waitFor(() => expect(result.current.connected).toBe(true))

    expect(hub.calls.slice(0, 2)).toEqual(['start', 'JoinTask:t1']) // join happens before any fetch
    expect(getEvents).toHaveBeenCalledWith('t1', { afterId: 0, take: 1000 })
    expect(ids(result.current.events)).toEqual([1, 2, 3, 4])
    expect(ids(result.current.processes)).toEqual([1, 2])
    expect(result.current.processes.find((p) => p.id === 2)!.endedAt).toBe('2026-10-19T00:01:00Z') // buffered upsert wins
    expect(result.current.status?.status).toBe('Running')

    // Live after sync: overlapping + new events in one batch, and a replayed one.
    hub.push('events', [ev(4), ev(5), ev(6)])
    hub.push('events', [ev(6)])
    hub.push('process', proc(3, { parentId: 1 }))
    hub.push('process', proc(3, { parentId: 1 }))
    expect(ids(result.current.events)).toEqual([1, 2, 3, 4, 5, 6])
    expect(ids(result.current.processes)).toEqual([1, 2, 3])
  })

  it('pages the events backlog until a short page', async () => {
    const hub = new MockHub()
    getProcesses.mockResolvedValue([])
    const page1 = Array.from({ length: 1000 }, (_, i) => ev(i + 1))
    getEvents.mockResolvedValueOnce(page1).mockResolvedValueOnce([ev(1001), ev(1002)])
    const { result } = renderHook(() => useTaskLive('t1', { createConnection: () => hub }))
    await waitFor(() => expect(result.current.connected).toBe(true))
    expect(getEvents).toHaveBeenNthCalledWith(2, 't1', { afterId: 1000, take: 1000 })
    expect(result.current.events).toHaveLength(1002)
  })

  it('rejoins and re-merges the backlog after a reconnect, without duplicates or gaps', async () => {
    const hub = new MockHub()
    getProcesses.mockResolvedValue([proc(1)])
    getEvents.mockResolvedValueOnce([ev(1), ev(2)])
    const { result } = renderHook(() => useTaskLive('t1', { createConnection: () => hub }))
    await waitFor(() => expect(result.current.connected).toBe(true))

    act(() => hub.reconnecting.forEach((f) => f()))
    expect(result.current.connected).toBe(false)

    // Missed events 3-4 while disconnected; event 5 arrives live during the re-sync.
    getProcesses.mockResolvedValue([proc(1), proc(2)])
    getEvents.mockResolvedValueOnce([ev(1), ev(2), ev(3), ev(4)])
    hub.onJoin = () => hub.push('events', [ev(4), ev(5)])
    await act(async () => hub.reconnected.forEach((f) => f()))
    await waitFor(() => expect(result.current.connected).toBe(true))

    expect(hub.calls.filter((c) => c === 'JoinTask:t1')).toHaveLength(2)
    expect(ids(result.current.events)).toEqual([1, 2, 3, 4, 5])
    expect(ids(result.current.processes)).toEqual([1, 2])
  })

  it('leaves the task and stops the connection on unmount', async () => {
    const hub = new MockHub()
    getProcesses.mockResolvedValue([])
    getEvents.mockResolvedValue([])
    const { result, unmount } = renderHook(() => useTaskLive('t1', { createConnection: () => hub }))
    await waitFor(() => expect(result.current.connected).toBe(true))
    unmount()
    await waitFor(() => expect(hub.stopped).toBe(true))
    expect(hub.calls).toContain('LeaveTask:t1')
  })

  it('keeps every event under StrictMode (state updaters run twice and must be pure)', async () => {
    const hub = new MockHub()
    getProcesses.mockResolvedValue([proc(1)])
    getEvents.mockResolvedValue([ev(1), ev(2), ev(3)])
    const { result } = renderHook(() => useTaskLive('t1', { createConnection: () => hub }), { wrapper: StrictMode })
    await waitFor(() => expect(result.current.connected).toBe(true))
    expect(ids(result.current.events)).toEqual([1, 2, 3])

    hub.push('events', [ev(3), ev(4), ev(5)])
    hub.push('events', [ev(6)])
    expect(ids(result.current.events)).toEqual([1, 2, 3, 4, 5, 6])
    expect(ids(result.current.processes)).toEqual([1])
  })

  it('retries a failed sync, keeping what arrived live meanwhile', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const hub = new MockHub()
    getProcesses.mockResolvedValue([proc(1)])
    getEvents.mockRejectedValueOnce(new Error('500')).mockResolvedValue([ev(1), ev(2)])
    const { result } = renderHook(() => useTaskLive('t1', { createConnection: () => hub }))
    await waitFor(() => expect(getEvents).toHaveBeenCalledTimes(1))
    hub.push('events', [ev(3)]) // buffered while the sync is failed/pending
    expect(result.current.connected).toBe(false)

    await act(async () => { await vi.advanceTimersByTimeAsync(2100) })
    await waitFor(() => expect(result.current.connected).toBe(true))
    expect(hub.calls.filter((c) => c === 'JoinTask:t1')).toHaveLength(2)
    expect(ids(result.current.events)).toEqual([1, 2, 3])
  })

  it('restarts the connection after it closes for good, then re-syncs', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const hub = new MockHub()
    getProcesses.mockResolvedValue([])
    getEvents.mockResolvedValueOnce([ev(1)]).mockResolvedValueOnce([ev(1), ev(2)])
    const { result } = renderHook(() => useTaskLive('t1', { createConnection: () => hub }))
    await waitFor(() => expect(result.current.connected).toBe(true))

    act(() => hub.closed.forEach((f) => f()))
    expect(result.current.connected).toBe(false)
    await act(async () => { await vi.advanceTimersByTimeAsync(2100) })
    await waitFor(() => expect(result.current.connected).toBe(true))
    expect(hub.startCount).toBe(2)
    expect(ids(result.current.events)).toEqual([1, 2])
  })
})
