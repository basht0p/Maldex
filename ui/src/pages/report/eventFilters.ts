import { useEffect, useState } from 'react'
import type { EventType, LiveEventDto } from '../../api/types'
import { useUrlState, type UrlField } from '../../kit/useUrlState'

const DEBOUNCE_MS = 300
export const EVENT_TYPES: readonly EventType[] = ['Process', 'File', 'Registry', 'Network', 'Dns']

const schema = {
  etype: { kind: 'enum', values: ['', ...EVENT_TYPES], default: '' },
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
  const [written, setWritten] = useState(values.eq) // the last `eq` this hook put in the URL
  const [seen, setSeen] = useState(values.eq)
  // An `eq` that arrives from elsewhere (back/forward, a link) replaces the typed text instead of being overwritten by it.
  if (values.eq !== seen) {
    setSeen(values.eq)
    if (values.eq !== written) {
      setText(values.eq)
      setWritten(values.eq)
    }
  }
  useEffect(() => {
    if (text === values.eq) return
    const t = setTimeout(() => {
      setWritten(text)
      set({ eq: text })
    }, DEBOUNCE_MS)
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

/** The same rules as the server: exact type, exact process id, case-insensitive summary substring. */
export function eventMatches(e: LiveEventDto, f: EventFilters): boolean {
  if (f.type && e.type !== f.type) return false
  if (f.processId !== undefined && e.processId !== f.processId) return false
  if (f.q && !e.summary.toLowerCase().includes(f.q.toLowerCase())) return false
  return true
}
