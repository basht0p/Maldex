import { useMemo, useState } from 'react'
import type { Verdict } from '../../api/types'
import { safeText } from '../../util/safeText'

/** The analyst-facing verdict label; mirrors ReportModel.LabelOf on the server. */
export function verdictLabel(verdict: Verdict): string {
  switch (verdict) {
    case 'NoThreats': return 'No threats detected'
    case 'Suspicious': return 'Suspicious'
    case 'Malicious': return 'Malicious'
    default: return 'Unknown'
  }
}

/** `safeText` for a multi-line block (`<pre>`): line breaks survive, every other control character is made visible. */
export function safeLines(s: string | null | undefined): string {
  return (s ?? '').split(/\r?\n/).map(safeText).join('\n')
}

export const TRUNCATED_NOTICE = 'Showing the first 2,000. Download the full report for the rest.'

type Key = string | number

/** Client-side sort of a report list. `keys` maps a field name to the value it sorts by. */
export function useSorted<T>(rows: readonly T[], keys: Record<string, (row: T) => Key>, initial: { sort: string; dir: 'asc' | 'desc' }) {
  const [state, setState] = useState(initial)
  const sorted = useMemo(() => {
    const key = keys[state.sort]
    if (!key) return rows
    const sign = state.dir === 'asc' ? 1 : -1
    // Index tiebreak keeps the server order stable for equal keys.
    return rows
      .map((row, i) => ({ row, i, k: key(row) }))
      .sort((a, b) => (a.k < b.k ? -sign : a.k > b.k ? sign : a.i - b.i))
      .map((x) => x.row)
  }, [rows, keys, state])
  const onSort = (sort: string, dir: 'asc' | 'desc') => setState({ sort, dir })
  return { sorted, sort: state.sort, dir: state.dir, onSort }
}

export function formatTime(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleTimeString()
}

