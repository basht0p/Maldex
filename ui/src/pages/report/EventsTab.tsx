import { useEffect, useState } from 'react'
import { getEvents } from '../../api/client'
import type { LiveEventDto, TaskReportDto } from '../../api/types'
import { EventTable } from '../../components/EventTable'
import { ErrorBanner } from '../../kit/ErrorBanner'
import { Skeleton } from '../../kit/Skeleton'
import { messageOf } from '../../util/apiMessage'

const PAGE = 500

/** Placeholder: the first page of events in the existing table; Task 12 replaces the body. */
export function EventsTab({ taskId }: { taskId: string; report: TaskReportDto; live?: boolean }) {
  const [events, setEvents] = useState<LiveEventDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [reload, setReload] = useState(0)
  useEffect(() => {
    let cancelled = false
    getEvents(taskId, { take: PAGE }).then(
      (page) => { if (!cancelled) setEvents(page) },
      (e: unknown) => { if (!cancelled) setError(messageOf(e).join(' ')) },
    )
    return () => { cancelled = true }
  }, [taskId, reload])

  if (error) return <ErrorBanner message={error} onRetry={() => { setError(null); setReload((n) => n + 1) }} />
  if (!events) return <Skeleton rows={4} />
  return <EventTable events={events} />
}
