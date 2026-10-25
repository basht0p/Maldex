import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { getSystemStatus } from '../api/client'
import type { SystemStatusDto } from '../api/types'
import { SystemStatusContext, needsAttention, type SystemStatusValue } from './useSystemStatus'

const POLL_MS = 5000

/** Polls the admin status endpoint every 5 s while `enabled`; pauses while the tab is hidden. */
export function SystemStatusProvider({ enabled, children }: { enabled: boolean; children: ReactNode }) {
  const [status, setStatus] = useState<SystemStatusDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const seq = useRef(0)
  const inFlight = useRef(false)

  const load = useCallback(() => {
    if (inFlight.current) return // a slow server still gets to answer
    inFlight.current = true
    const mine = seq.current
    const done = () => { if (mine === seq.current) inFlight.current = false }
    getSystemStatus().then(
      (s) => { if (mine === seq.current) { setStatus(s); setError(null) } done() },
      (e: unknown) => { if (mine === seq.current) setError(e instanceof Error ? e.message : 'Could not load system status.'); done() },
    )
  }, [])

  const reset = useCallback(() => {
    seq.current++
    inFlight.current = false
    setStatus(null)
    setError(null)
  }, [])

  useEffect(() => {
    if (!enabled) return
    if (!document.hidden) load()
    const id = setInterval(() => { if (!document.hidden) load() }, POLL_MS)
    const onVisible = () => { if (!document.hidden) load() }
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      clearInterval(id)
      document.removeEventListener('visibilitychange', onVisible)
      reset()
    }
  }, [enabled, load, reset])

  const value = useMemo<SystemStatusValue>(
    () => ({ status, error, refresh: load, attention: needsAttention(status) }),
    [status, error, load],
  )
  return <SystemStatusContext.Provider value={value}>{children}</SystemStatusContext.Provider>
}
