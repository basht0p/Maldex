import { useEffect, useMemo, useState } from 'react'
import { eventsExportUrl, getEvents } from '../../api/client'
import type { LiveEventDto, TaskReportDto } from '../../api/types'
import { EventTable } from '../../components/EventTable'
import { ErrorBanner } from '../../kit/ErrorBanner'
import { Skeleton } from '../../kit/Skeleton'
import { messageOf } from '../../util/apiMessage'
import { safeText } from '../../util/safeText'
import { EVENT_TYPES, eventMatches, useEventFilters, type EventFilterState, type ProcessOption } from './eventFilters'
import styles from './EventsTab.module.css'

const PAGE = 500
const NO_MATCH = 'No events match these filters'

const basename = (p: string) => p.split(/[\\/]/).pop() ?? p

/** Type chips, process select and the text search. */
export function EventFilterBar({ processes, bar }: { processes: ProcessOption[]; bar: EventFilterState }) {
  const { type, process, text, setText, patch } = bar
  const known = processes.some((p) => String(p.id) === process)
  return (
    <div className={styles.eventBar}>
      <div className={styles.chips} role="group" aria-label="Event types">
        {(['', ...EVENT_TYPES] as const).map((t) => (
          <button key={t || 'all'} type="button" className={`${styles.chip} ${type === t ? styles.chipOn : ''}`} aria-pressed={type === t} onClick={() => patch({ etype: t })}>
            {t || 'All'}
          </button>
        ))}
      </div>
      <select aria-label="Process" value={process} onChange={(e) => patch({ process: e.target.value })}>
        <option value="">All processes</option>
        {process !== '' && !known && <option value={process}>Process {safeText(process)}</option>}
        {processes.map((p) => (
          <option key={p.id} value={String(p.id)}>{p.pid} {safeText(basename(p.imagePath))}</option>
        ))}
      </select>
      <input type="search" data-search aria-label="Search events" placeholder="Search summary" value={text} onChange={(e) => setText(e.target.value)} />
    </div>
  )
}

/** The running layout: pushed events, filtered client-side; processes come from the live tree. */
export function LiveEventsPanel({ events, processes }: { events: LiveEventDto[]; processes: ProcessOption[] }) {
  const bar = useEventFilters()
  const { type, processId, q } = bar.filters
  const shown = useMemo(() => events.filter((e) => eventMatches(e, { type, processId, q })), [events, type, processId, q])
  const filtered = type !== undefined || processId !== undefined || q !== undefined
  return (
    <div>
      <EventFilterBar processes={processes} bar={bar} />
      <EventTable events={shown} controls={false} emptyTitle={filtered && events.length > 0 ? NO_MATCH : undefined} />
    </div>
  )
}

interface Result {
  key: string
  events: LiveEventDto[]
  more: boolean
}

export function EventsTab({ taskId, report, live, liveEvents, liveProcesses }: {
  taskId: string
  report: TaskReportDto
  live?: boolean
  liveEvents?: LiveEventDto[]
  liveProcesses?: ProcessOption[]
}) {
  const bar = useEventFilters()
  const { type, processId, q } = bar.filters
  const key = JSON.stringify([taskId, type, processId, q])
  const [result, setResult] = useState<Result | null>(null)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<{ key: string; message: string } | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    if (live) return
    let cancelled = false
    getEvents(taskId, { take: PAGE, type, processId, q }).then(
      (page) => { if (!cancelled) setResult({ key, events: page, more: page.length === PAGE }) },
      (e: unknown) => { if (!cancelled) setError({ key, message: messageOf(e).join(' ') }) },
    )
    return () => { cancelled = true }
  }, [taskId, live, type, processId, q, key, reload])

  async function loadMore() {
    if (!result || result.key !== key || result.events.length === 0) return
    const requestKey = key
    setLoadingMore(true)
    try {
      const page = await getEvents(taskId, { take: PAGE, afterId: result.events[result.events.length - 1].id, type, processId, q })
      // A response for filters that are no longer active is dropped.
      setResult((prev) => (prev && prev.key === requestKey ? { key: requestKey, events: prev.events.concat(page), more: page.length === PAGE } : prev))
    } catch (e) {
      setError({ key: requestKey, message: messageOf(e).join(' ') })
    } finally {
      setLoadingMore(false)
    }
  }

  if (live) return <LiveEventsPanel events={liveEvents ?? []} processes={liveProcesses ?? []} />

  const current = result?.key === key ? result : null
  const failure = error?.key === key ? error.message : null
  const filtered = type !== undefined || processId !== undefined || q !== undefined
  const exportFilters = { type, processId, q }
  return (
    <div>
      <EventFilterBar processes={report.processes} bar={bar} />
      <div className={styles.eventExport}>
        <a href={eventsExportUrl(taskId, 'csv', exportFilters)} download>Export CSV</a>
        <a href={eventsExportUrl(taskId, 'json', exportFilters)} download>Export JSON</a>
      </div>
      {failure ? (
        <ErrorBanner message={failure} onRetry={() => { setError(null); setReload((n) => n + 1) }} />
      ) : !current ? (
        <Skeleton rows={4} />
      ) : (
        <>
          <EventTable events={current.events} controls={false} emptyTitle={filtered ? NO_MATCH : undefined} />
          {current.more && (
            <button type="button" className={styles.loadMore} disabled={loadingMore} onClick={() => void loadMore()}>
              {loadingMore ? 'Loading…' : 'Load more'}
            </button>
          )}
        </>
      )}
    </div>
  )
}
