import { useEffect, useState } from 'react'
import { getTemplateJob } from '../api/client'
import type { TemplateJobDto } from '../api/types'
import { createHubConnection, type HubConnectionLike } from './useTaskLive'

export interface TemplateJobLive {
  job: TemplateJobDto | null
  /** The job could not be loaded (not found, or the request failed). */
  loadError: string | null
}

const RETRY_MS = 2000

/**
 * Live view of a template job: joins the job group on the hub (`JoinTemplateJob`), loads the current state over REST
 * and applies every `templateJob` push. The server pushes the full DTO on every change and the REST load happens after
 * the join, so a push that lands while the load is in flight is at least as new as the load and wins over it.
 */
export function useTemplateJobLive(jobId: string | null, createConnection: () => HubConnectionLike = createHubConnection): TemplateJobLive {
  const [factory] = useState(() => createConnection) // fixed at mount: an inline factory must not tear the connection down
  const [job, setJob] = useState<TemplateJobDto | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)

  useEffect(() => {
    if (jobId === null) return
    let cancelled = false
    let retry: ReturnType<typeof setTimeout> | undefined
    let pushed = false
    const conn = factory()
    conn.on('templateJob', (dto: TemplateJobDto) => {
      if (cancelled || dto.id !== jobId) return
      pushed = true
      setJob(dto)
      setLoadError(null)
    })

    const sync = async () => {
      pushed = false
      try {
        await conn.invoke('JoinTemplateJob', jobId)
        const dto = await getTemplateJob(jobId)
        if (cancelled) return
        if (!pushed) setJob(dto)
        setLoadError(null)
      } catch (e) {
        if (!cancelled) setLoadError(e instanceof Error ? e.message : 'Could not load the job.')
      }
    }
    const connect = () => {
      conn.start().then(
        () => (cancelled ? undefined : sync()),
        () => {
          if (!cancelled) retry = setTimeout(connect, RETRY_MS)
        },
      )
    }
    conn.onreconnecting(() => undefined)
    conn.onreconnected(() => void sync())
    conn.onclose(() => {
      if (!cancelled) retry = setTimeout(connect, RETRY_MS)
    })
    connect()

    return () => {
      cancelled = true
      clearTimeout(retry)
      setJob(null)
      setLoadError(null)
      void conn.stop().catch(() => undefined)
    }
  }, [jobId, factory])

  return { job, loadError }
}
