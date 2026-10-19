import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useEffect, useState } from 'react'
import { getEvents, getProcesses } from '../api/client'
import type { LiveEventDto, ProcessNodeDto, TaskStatusUpdate } from '../api/types'
import { getAccessToken } from '../auth/auth'

/** The subset of the SignalR HubConnection this hook uses, so tests can drive it with a mock. */
export interface HubConnectionLike {
  on(methodName: string, callback: (...args: never[]) => void): void
  onreconnecting(callback: () => void): void
  onreconnected(callback: () => void): void
  onclose(callback: () => void): void
  start(): Promise<void>
  stop(): Promise<void>
  invoke(methodName: string, ...args: unknown[]): Promise<unknown>
}

export interface UseTaskLiveOptions {
  /** Creates the hub connection (not yet started). Defaults to a real SignalR connection to /hubs/tasks. */
  createConnection?: () => HubConnectionLike
}

export interface TaskLive {
  status: TaskStatusUpdate | null
  processes: ProcessNodeDto[]
  events: LiveEventDto[]
  connected: boolean
}

/** Must equal the server's per-request cap (LiveControlEndpoints.MaxEventsPerRequest); a shorter page means "no more". */
const PAGE = 1000
const RETRY_MS = 2000
const MAX_RETRY_MS = 15000

export function createHubConnection(): HubConnectionLike {
  return new HubConnectionBuilder()
    .withUrl('/hubs/tasks', { accessTokenFactory: async () => (await getAccessToken()) ?? '' })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()
}

/**
 * Appends the events whose ids are not already in `prev` (or earlier in `incoming`), keeping the list ascending by id.
 * Returns `prev` when nothing is new. PURE: it is used inside setState updaters, which React runs twice under StrictMode.
 */
export function mergeEvents(prev: LiveEventDto[], incoming: LiveEventDto[]): LiveEventDto[] {
  const seen = new Set(prev.map((e) => e.id))
  const fresh: LiveEventDto[] = []
  for (const e of incoming) {
    if (seen.has(e.id)) continue
    seen.add(e.id)
    fresh.push(e)
  }
  if (fresh.length === 0) return prev
  const merged = prev.concat(fresh)
  const lastPrev = prev.length > 0 ? prev[prev.length - 1].id : -Infinity
  const ordered = fresh[0].id > lastPrev && fresh.every((e, i) => i === 0 || e.id > fresh[i - 1].id)
  return ordered ? merged : merged.sort((a, b) => a.id - b.id)
}

/** Upserts by process id (a node is pushed again when it exits), keeping first-seen order. */
export function upsertProcesses(prev: ProcessNodeDto[], incoming: ProcessNodeDto[]): ProcessNodeDto[] {
  if (incoming.length === 0) return prev
  const next = prev.slice()
  const index = new Map(next.map((p, i) => [p.id, i]))
  for (const p of incoming) {
    const at = index.get(p.id)
    if (at === undefined) {
      index.set(p.id, next.length)
      next.push(p)
    } else {
      next[at] = p
    }
  }
  return next
}

type Buffered =
  | { kind: 'status'; value: TaskStatusUpdate }
  | { kind: 'process'; value: ProcessNodeDto }
  | { kind: 'events'; value: LiveEventDto[] }

/**
 * Live view of a task. Rejoin protocol (see TaskHub): JoinTask first, buffer live callbacks, fetch the REST backlog,
 * apply it, then the buffer (events deduplicated by id, processes upserted by id). Repeated after every reconnect.
 */
export function useTaskLive(taskId: string, options: UseTaskLiveOptions = {}): TaskLive {
  // Fixed at mount: an inline factory (a new function every render) must not tear the connection down.
  const [factory] = useState(() => options.createConnection ?? createHubConnection)
  const [status, setStatus] = useState<TaskStatusUpdate | null>(null)
  const [processes, setProcesses] = useState<ProcessNodeDto[]>([])
  const [events, setEvents] = useState<LiveEventDto[]>([])
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    let cancelled = false
    let syncing = false
    let generation = 0
    let buffer: Buffered[] = []
    let syncRetry: ReturnType<typeof setTimeout> | undefined
    let connectRetry: ReturnType<typeof setTimeout> | undefined
    let syncFailures = 0
    const conn = factory()

    const apply = (m: Buffered) => {
      if (m.kind === 'status') setStatus(m.value)
      else if (m.kind === 'process') setProcesses((prev) => upsertProcesses(prev, [m.value]))
      else setEvents((prev) => mergeEvents(prev, m.value))
    }
    const live = (m: Buffered) => {
      if (cancelled) return
      if (syncing) buffer.push(m)
      else apply(m)
    }

    conn.on('status', (value: TaskStatusUpdate) => live({ kind: 'status', value }))
    conn.on('process', (value: ProcessNodeDto) => live({ kind: 'process', value }))
    conn.on('events', (value: LiveEventDto[]) => live({ kind: 'events', value }))

    const sync = async () => {
      const mine = ++generation
      clearTimeout(syncRetry)
      syncing = true // the buffer is kept across retries: a status push is not in any backlog, and event dupes are dropped by id
      try {
        await conn.invoke('JoinTask', taskId) // 1. join first: from now on everything saved is pushed to us
        const procs = await getProcesses(taskId) // 3. backlog (live callbacks accumulate in `buffer` meanwhile)
        const backlog: LiveEventDto[] = []
        let afterId = 0
        for (;;) {
          const page = await getEvents(taskId, { afterId, take: PAGE })
          backlog.push(...page)
          if (page.length < PAGE) break
          afterId = page[page.length - 1].id
        }
        if (cancelled || mine !== generation) return
        setProcesses((prev) => upsertProcesses(prev, procs)) // 4. backlog, then the buffer
        setEvents((prev) => mergeEvents(prev, backlog))
        for (const m of buffer) apply(m)
        buffer = []
        syncing = false
        syncFailures = 0
        setConnected(true)
      } catch {
        // Join/backlog failed (a 5xx, or the connection dropped mid-sync). Stay disconnected, keep buffering, and retry
        // with a short backoff; a reconnect meanwhile starts its own sync, which supersedes this one.
        if (!cancelled && mine === generation) {
          setConnected(false)
          syncRetry = setTimeout(() => void sync(), Math.min(RETRY_MS * 2 ** syncFailures, MAX_RETRY_MS))
          syncFailures++
        }
      }
    }

    conn.onreconnecting(() => {
      if (cancelled) return
      syncing = true // anything delivered before the re-sync completes is buffered, not rendered
      setConnected(false)
    })
    conn.onreconnected(() => {
      if (!cancelled) void sync()
    })
    // SignalR's automatic reconnect only covers an established connection (and gives up after about 42 s), so the first
    // start and a start after `onclose` are retried here until they succeed.
    const connect = () => {
      conn.start().then(
        () => (cancelled ? undefined : sync()),
        () => {
          if (!cancelled) connectRetry = setTimeout(connect, RETRY_MS)
        },
      )
    }
    conn.onclose(() => {
      if (cancelled) return
      generation++ // abandon any sync in flight
      syncing = true
      setConnected(false)
      clearTimeout(syncRetry)
      connectRetry = setTimeout(connect, RETRY_MS)
    })
    connect()

    return () => {
      cancelled = true
      clearTimeout(syncRetry)
      clearTimeout(connectRetry)
      // Reset so a different task id never shows the previous task's data.
      setStatus(null)
      setProcesses([])
      setEvents([])
      setConnected(false)
      void conn
        .invoke('LeaveTask', taskId)
        .catch(() => undefined)
        .then(() => conn.stop())
        .catch(() => undefined)
    }
  }, [taskId, factory])

  return { status, processes, events, connected }
}
