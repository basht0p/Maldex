import { useEffect, useMemo, useState } from 'react'
import { eventsExportUrl, getEvents } from '../../api/client'
import type { EventType, LiveEventDto, TaskReportDto } from '../../api/types'
import { EventTable } from '../../components/EventTable'
import { ErrorBanner } from '../../kit/ErrorBanner'
import { Skeleton } from '../../kit/Skeleton'
import { useUrlState, type UrlField } from '../../kit/useUrlState'
import { messageOf } from '../../util/apiMessage'
import { safeText } from '../../util/safeText'
import styles from './EventsTab.module.css'

const PAGE = 500
const DEBOUNCE_MS = 300
const TYPES: readonly EventType[] = ['Process', 'File', 'Registry', 'Network', 'Dns']

const schema = {
  etype: { kind: 'enum', values: ['', ...TYPES], default: '' },
  process: { kind: 'string', default: '' },
  eq: { kind: 'string', default: '' },
} satisfies Record<string, UrlField>

export interface ProcessOption {
  id: number
  pid: number
  imagePath: string
}

export interface EventFilters {
  type?: EventType
  processId?: number
  q?: string
}

export interface EventFilterState {
  filters: EventFilters
  type: '' | EventType
  process: string
  text: string
  setText: (t: string) => void
  patch: (p: { etype?: string; process?: string; eq?: string }) => void
}

/** The filter state of the Events views; it lives in the URL (`etype`, `process`, `eq`), the search text debounced. */
export function useEventFilters(): EventFilterState {
  const [values, set] = useUrlState(schema)
  const [text, setText] = useState(values.eq)
  useEffect(() => {
    if (text === values.eq) return
    const t = setTimeout(() => set({ eq: text }), DEBOUNCE_MS)
    return () => clearTimeout(t)
  }, [text, values.eq, set])
  const q = values.eq.trim() // the server rejects a whitespace-only q
  const id = Number(values.process)
  const filters: EventFilters = {
    type: values.etype === '' ? undefined : (values.etype as EventType),
    processId: values.process !== '' && Number.isInteger(id) && id > 0 ? id : undefined,
    q: q === '' ? undefined : q,
  }
  return { filters, type: values.etype as '' | EventType, process: values.process, text, setText, patch: set }
}

const basename = (p: string) => p.split(/[\\/]/).pop() ?? p

/** Type chips, process select and the text search. */
export function EventFilterBar({ processes, bar }: { processes: ProcessOption[]; bar: EventFilterState }) {
  const { type, process, text, setText, patch } = bar
  const known = processes.some((p) => String(p.id) === process)
  return (
    <div className={styles.eventBar}>
      <div className={styles.chips} role="group" aria-label="Event types">
        {(['', ...TYPES] as const).map((t) => (
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
      <input type="search" aria-label="Search events" placeholder="Search summary" value={text} onChange={(e) => setText(e.target.value)} />
    </div>
  )
}

/** The same rules as the server: exact type, exact process id, case-insensitive summary substring. */
export function eventMatches(e: LiveEventDto, f: EventFilters): boolean {
  if (f.type && e.type !== f.type) return false
  if (f.processId !== undefined && e.processId !== f.processId) return false
  if (f.q && !e.summary.toLowerCase().includes(f.q.toLowerCase())) return false
  return true
}

/** The running layout: pushed events, filtered client-side. */
export function LiveEventsPanel({ events, processes }: { events: LiveEventDto[]; processes: ProcessOption[] }) {
  const bar = useEventFilters()
  const { type, processId, q } = bar.filters
  const shown = useMemo(() => events.filter((e) => eventMatches(e, { type, processId, q })), [events, type, processId, q])
  return (
    <div>
      <EventFilterBar processes={processes} bar={bar} />
      <EventTable events={shown} controls={false} />
    </div>
  )
}

export function EventsTab({ taskId, report, live, liveEvents }: { taskId: string; report: TaskReportDto; live?: boolean; liveEvents?: LiveEventDto[] }) {
  const bar = useEventFilters()
  const { type, processId, q } = bar.filters
  const [events, setEvents] = useState<LiveEventDto[] | null>(null)
  const [more, setMore] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    if (live) return
    let cancelled = false
    setEvents(null)
    getEvents(taskId, { take: PAGE, type, processId, q }).then(
      (page) => {
        if (cancelled) return
        setEvents(page)
        setMore(page.length === PAGE)
        setError(null)
      },
      (e: unknown) => { if (!cancelled) setError(messageOf(e).join(' ')) },
    )
    return () => { cancelled = true }
  }, [taskId, live, type, processId, q, reload])

  async function loadMore() {
    if (!events || events.length === 0) return
    setLoadingMore(true)
    try {
      const page = await getEvents(taskId, { take: PAGE, afterId: events[events.length - 1].id, type, processId, q })
      setEvents((prev) => (prev ? prev.concat(page) : page))
      setMore(page.length === PAGE)
    } catch (e) {
      setError(messageOf(e).join(' '))
    } finally {
      setLoadingMore(false)
    }
  }

  if (live) return <LiveEventsPanel events={liveEvents ?? []} processes={report.processes} />

  const exportFilters = { type, processId, q }
  return (
    <div>
      <EventFilterBar processes={report.processes} bar={bar} />
      <div className={styles.eventExport}>
        <a href={eventsExportUrl(taskId, 'csv', exportFilters)} download>Export CSV</a>
        <a href={eventsExportUrl(taskId, 'json', exportFilters)} download>Export JSON</a>
      </div>
      {error ? (
        <ErrorBanner message={error} onRetry={() => { setError(null); setReload((n) => n + 1) }} />
      ) : !events ? (
        <Skeleton rows={4} />
      ) : (
        <>
          <EventTable events={events} controls={false} />
          {more && (
            <button type="button" className={styles.loadMore} disabled={loadingMore} onClick={() => void loadMore()}>
              {loadingMore ? 'Loading…' : 'Load more'}
            </button>
          )}
        </>
      )}
    </div>
  )
}
