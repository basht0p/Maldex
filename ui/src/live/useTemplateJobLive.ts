import { useCallback, useEffect, useState } from 'react'
import { getTemplateJob } from '../api/client'
import type { TemplateJobDto } from '../api/types'
import { createHubConnection, type HubConnectionLike } from './useTaskLive'

export interface TemplateJobLive {
  job: TemplateJobDto | null
  /** The job could not be loaded (not found, or the request failed). */
  loadError: string | null
  /** Starts the connect, join and load over again (the Retry of a load error). */
  retry: () => void
}

/**
 * A newer DTO over the held one. The hub pushes can lack the history and the requester (`requestedByName` is null in
 * coordinator pushes), so the held steps and name survive a DTO that has none or fewer steps.
 */
export function mergeJob(held: TemplateJobDto | null, next: TemplateJobDto): TemplateJobDto {
  if (!held || held.id !== next.id) return next
  const heldSteps = held.steps ?? []
  const keepSteps = (next.steps?.length ?? 0) < heldSteps.length
  return {
    ...next,
    steps: keepSteps ? held.steps : next.steps,
    requestedByName: next.requestedByName ?? held.requestedByName,
  }
}

/** Backoff between attempts to connect, join or load; the last step repeats until success or unmount. */
const RETRY_MS = [2000, 5000, 10000, 30000]

/**
 * Live view of a template job: joins the job group on the hub (`JoinTemplateJob`), loads the current state over REST
 * and applies every `templateJob` push. The server pushes the full DTO on every change and the REST load happens after
 * the join, so a push that lands while the load is in flight is at least as new as the load and wins over it.
 */
export function useTemplateJobLive(jobId: string | null, createConnection: () => HubConnectionLike = createHubConnection): TemplateJobLive {
  const [factory] = useState(() => createConnection) // fixed at mount: an inline factory must not tear the connection down
  const [job, setJob] = useState<TemplateJobDto | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    if (jobId === null) return
    let cancelled = false
    let retry: ReturnType<typeof setTimeout> | undefined
    let pushed = false
    let failures = 0
    const conn = factory()
    conn.on('templateJob', (dto: TemplateJobDto) => {
      if (cancelled || dto.id !== jobId) return
      pushed = true
      setJob((held) => mergeJob(held, dto))
      setLoadError(null)
    })

    const later = (action: () => void) => {
      if (cancelled) return
      clearTimeout(retry)
      retry = setTimeout(action, RETRY_MS[Math.min(failures, RETRY_MS.length - 1)])
      failures++
    }

    const sync = async () => {
      pushed = false
      try {
        await conn.invoke('JoinTemplateJob', jobId)
        const dto = await getTemplateJob(jobId)
        if (cancelled) return
        setJob((held) => (pushed ? held : mergeJob(held, dto)))
        setLoadError(null)
        failures = 0
      } catch (e) {
        if (cancelled) return
        setLoadError(e instanceof Error ? e.message : 'Could not load the job.')
        later(() => void sync()) // a failed join or load on a live connection is retried, not left stuck
      }
    }
    const connect = () => {
      conn.start().then(
        () => (cancelled ? undefined : sync()),
        () => later(connect),
      )
    }
    conn.onreconnecting(() => undefined)
    conn.onreconnected(() => void sync())
    conn.onclose(() => later(connect))
    connect()

    return () => {
      cancelled = true
      clearTimeout(retry)
      setJob(null)
      setLoadError(null)
      void conn.stop().catch(() => undefined)
    }
  }, [jobId, factory, attempt])

  const retry = useCallback(() => setAttempt((n) => n + 1), [])
  return { job, loadError, retry }
}
