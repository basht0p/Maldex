import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { getSystemStatus } from '../api/client'
import type { SystemStatusDto } from '../api/types'

const POLL_MS = 5000
const LOW_DISK_FRACTION = 0.1

export interface SystemStatusValue {
  status: SystemStatusDto | null
  error: string | null
  refresh(): void
  attention: boolean
}

const noop = () => undefined
const Ctx = createContext<SystemStatusValue>({ status: null, error: null, refresh: noop, attention: false })

function needsAttention(s: SystemStatusDto | null): boolean {
  if (!s) return false
  return s.workers.some((w) => w.stalled) || s.disks.some((d) => d.totalBytes > 0 && d.freeBytes / d.totalBytes < LOW_DISK_FRACTION)
}

/** Polls the admin status endpoint every 5 s while `enabled`; pauses while the tab is hidden. */
export function SystemStatusProvider({ enabled, children }: { enabled: boolean; children: ReactNode }) {
  const [status, setStatus] = useState<SystemStatusDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const seq = useRef(0)

  const load = useCallback(() => {
    const mine = ++seq.current
    getSystemStatus().then(
      (s) => { if (mine === seq.current) { setStatus(s); setError(null) } },
      (e: unknown) => { if (mine === seq.current) setError(e instanceof Error ? e.message : 'Could not load system status.') },
    )
  }, [])

  const invalidate = useCallback(() => { seq.current++ }, [])

  useEffect(() => {
    if (!enabled) return
    if (!document.hidden) load()
    const id = setInterval(() => { if (!document.hidden) load() }, POLL_MS)
    const onVisible = () => { if (!document.hidden) load() }
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      clearInterval(id)
      document.removeEventListener('visibilitychange', onVisible)
      invalidate()
    }
  }, [enabled, load, invalidate])

  const value = useMemo<SystemStatusValue>(
    () => ({ status, error, refresh: load, attention: needsAttention(status) }),
    [status, error, load],
  )
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export const useSystemStatus = () => useContext(Ctx)
