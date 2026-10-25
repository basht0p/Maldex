import { useMemo, useState } from 'react'
import type { EventType, LiveEventDto } from '../api/types'
import { EmptyState } from '../kit/EmptyState'
import { safeText } from '../util/safeText'
import styles from './EventTable.module.css'

const EVENT_TYPES: readonly EventType[] = ['Process', 'File', 'Registry', 'Network', 'Dns']

/** Above this many (filtered) rows only the visible window is rendered. */
const VIRTUALIZE_ABOVE = 500
const ROW_HEIGHT = 28
const VIEWPORT_HEIGHT = 480
const OVERSCAN = 10

export interface EventFilter {
  /** Initially selected type chips; empty or omitted means all types. */
  types?: readonly EventType[]
  /** Initial search text. */
  text?: string
}

/**
 * `controls={false}` hides the built-in chips and search (the Events tab and the running layout bring their own bar).
 * `onCopy` is called with the value after a hover copy button has copied it.
 */
export function EventTable({ events, filter, controls = true, onCopy, emptyTitle = 'No events yet.' }: { events: LiveEventDto[]; filter?: EventFilter; controls?: boolean; onCopy?: (value: string) => void; emptyTitle?: string }) {
  const [types, setTypes] = useState<ReadonlySet<EventType>>(() => new Set(filter?.types ?? []))
  const [text, setText] = useState(filter?.text ?? '')
  const [scrollTop, setScrollTop] = useState(0)

  const toggle = (t: EventType) =>
    setTypes((prev) => {
      const next = new Set(prev)
      if (!next.delete(t)) next.add(t)
      return next
    })

  const filtered = useMemo(() => {
    const needle = text.trim().toLowerCase()
    return events.filter((e) => (types.size === 0 || types.has(e.type)) && (!needle || e.summary.toLowerCase().includes(needle)))
  }, [events, types, text])

  const virtual = filtered.length > VIRTUALIZE_ABOVE
  const first = virtual ? Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN) : 0
  const last = virtual ? Math.min(filtered.length, Math.ceil((scrollTop + VIEWPORT_HEIGHT) / ROW_HEIGHT) + OVERSCAN) : filtered.length
  const visible = filtered.slice(first, last)

  const copy = (value: string) => {
    void navigator.clipboard?.writeText(value).then(() => onCopy?.(value), () => undefined)
  }

  if (events.length === 0) return <EmptyState title={emptyTitle} />

  return (
    <div>
      {controls && <div className={styles.controls}>
        <div className={styles.chips} role="group" aria-label="Event types">
          {EVENT_TYPES.map((t) => (
            <button key={t} type="button" className={`${styles.chip} ${types.has(t) ? styles.on : ''}`} aria-pressed={types.has(t)} onClick={() => toggle(t)}>
              {t}
            </button>
          ))}
        </div>
        <input type="search" aria-label="Search events" placeholder="Search summary" value={text} onChange={(e) => setText(e.target.value)} className={styles.search} />
        <span className={styles.count}>{filtered.length === events.length ? `${events.length} events` : `${filtered.length} of ${events.length} events`}</span>
      </div>}
      <div
        className={styles.scroller}
        style={virtual ? { height: VIEWPORT_HEIGHT } : { maxHeight: VIEWPORT_HEIGHT }}
        onScroll={virtual ? (e) => setScrollTop(e.currentTarget.scrollTop) : undefined}
      >
        <table className={styles.table}>
          <thead>
            <tr>
              <th>Time</th>
              <th>Type</th>
              <th>PID</th>
              <th>Summary</th>
            </tr>
          </thead>
          <tbody>
            {virtual && first > 0 && (
              <tr aria-hidden="true" style={{ height: first * ROW_HEIGHT }}>
                <td colSpan={4} />
              </tr>
            )}
            {visible.map((e) => (
              <tr key={e.id} style={virtual ? { height: ROW_HEIGHT } : undefined}>
                <td className={styles.time}>{formatTime(e.at)}</td>
                <td>{e.type}</td>
                <td className={styles.value}>{e.pid}<CopyCell label="Copy PID" onCopy={() => copy(String(e.pid))} /></td>
                <td className={styles.summary} title={safeText(e.summary)}>{safeText(e.summary)}<CopyCell label="Copy summary" onCopy={() => copy(e.summary)} /></td>
              </tr>
            ))}
            {virtual && last < filtered.length && (
              <tr aria-hidden="true" style={{ height: (filtered.length - last) * ROW_HEIGHT }}>
                <td colSpan={4} />
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function CopyCell({ label, onCopy }: { label: string; onCopy: () => void }) {
  return <button type="button" className={styles.copy} aria-label={label} title={label} onClick={onCopy}>⧉</button>
}

function formatTime(at: string): string {
  const d = new Date(at)
  return Number.isNaN(d.getTime()) ? safeText(at) : d.toLocaleTimeString()
}

export default EventTable
