import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { ApiError, cancelTask, listOrgs, listTasks } from '../api/client'
import { RUN_STATUSES, type OrgDto, type RunStatus, type TaskDto, type TaskListQuery, type Verdict } from '../api/types'
import { StatusBadge, VerdictBadge } from '../components/StatusBadge'
import { useConfirm } from '../kit/useConfirm'
import { CopyButton } from '../kit/CopyButton'
import { EmptyState } from '../kit/EmptyState'
import { ErrorBanner } from '../kit/ErrorBanner'
import { Duration, RelativeTime } from '../kit/RelativeTime'
import { Skeleton } from '../kit/Skeleton'
import { SortHeader } from '../kit/SortHeader'
import { useToast } from '../kit/useToast'
import { useUrlState, type UrlField } from '../kit/useUrlState'
import { messageOf } from '../util/apiMessage'
import { safeText } from '../util/safeText'
import styles from './forms.module.css'

const PAGE_SIZE = 25
const REFRESH_MS = 5000
const SEARCH_DEBOUNCE_MS = 300
const VERDICTS: readonly Verdict[] = ['Unknown', 'NoThreats', 'Suspicious', 'Malicious']

const SCHEMA = {
  q: { kind: 'string', default: '' },
  verdict: { kind: 'enum', values: ['', ...VERDICTS], default: '' },
  status: { kind: 'enum', values: ['', ...RUN_STATUSES], default: '' },
  org: { kind: 'string', default: '' },
  mine: { kind: 'boolean', default: false as boolean },
  from: { kind: 'date', default: '' },
  to: { kind: 'date', default: '' },
  sort: { kind: 'enum', values: ['created', 'verdict', 'duration'], default: 'created' },
  dir: { kind: 'enum', values: ['desc', 'asc'], default: 'desc' },
  page: { kind: 'number', default: 0, min: 0 },
} satisfies Record<string, UrlField>

function target(t: TaskDto): string {
  if (t.url) return safeText(t.url)
  if (t.originalFileName) return safeText(t.originalFileName)
  return t.sampleSha256 ? t.sampleSha256 : '(unnamed)'
}

export default function TasksPage() {
  const toast = useToast()
  const confirm = useConfirm()
  const [f, setF] = useUrlState(SCHEMA)
  const [orgs, setOrgs] = useState<OrgDto[]>([])
  const [items, setItems] = useState<TaskDto[]>([])
  const [total, setTotal] = useState(0)
  const [loaded, setLoaded] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const seq = useRef(0)

  // Typing is held locally and written to the URL after a pause.
  const [search, setSearch] = useState(f.q)
  const [seenQ, setSeenQ] = useState(f.q)
  if (f.q !== seenQ) {
    // The URL changed from outside (Clear filters, a link): follow it unless it already matches what is typed.
    setSeenQ(f.q)
    if (f.q !== search.trim()) setSearch(f.q)
  }
  useEffect(() => {
    const trimmed = search.trim()
    if (trimmed === f.q.trim()) return
    const id = setTimeout(() => setF({ q: trimmed, page: 0 }), SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(id)
  }, [search, f.q, setF])

  const q = f.q.trim()
  const skip = f.page * PAGE_SIZE

  const refresh = useCallback(async () => {
    const mine = ++seq.current
    try {
      const page = await listTasks({
        q: q || undefined,
        verdict: (f.verdict || undefined) as Verdict | undefined,
        status: (f.status || undefined) as RunStatus | undefined,
        orgId: f.org || undefined,
        mine: f.mine || undefined,
        from: f.from || undefined,
        to: f.to || undefined,
        sort: f.sort as TaskListQuery['sort'],
        dir: f.dir as TaskListQuery['dir'],
        skip,
        take: PAGE_SIZE,
      })
      if (mine !== seq.current) return
      setItems(page.items)
      setTotal(page.total)
      setError(null)
    } catch (e) {
      if (mine !== seq.current) return
      setError(messageOf(e).join(' '))
    } finally {
      if (mine === seq.current) setLoaded(true)
    }
  }, [q, f.verdict, f.status, f.org, f.mine, f.from, f.to, f.sort, f.dir, skip])

  useEffect(() => {
    listOrgs().then(setOrgs, () => undefined)
  }, [])

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- initial fetch + polling
    void refresh()
    let timer: ReturnType<typeof setInterval> | undefined
    const start = () => { timer = setInterval(() => void refresh(), REFRESH_MS) }
    const stop = () => { if (timer !== undefined) clearInterval(timer); timer = undefined }
    if (!document.hidden) start()
    const onVisibility = () => {
      stop()
      if (!document.hidden) { void refresh(); start() }
    }
    document.addEventListener('visibilitychange', onVisibility)
    return () => { stop(); document.removeEventListener('visibilitychange', onVisibility) }
  }, [refresh])

  async function onCancel(id: string) {
    const ok = await confirm({ title: 'Cancel this queued task?', body: 'It leaves the queue and will not run.', confirmLabel: 'Cancel task', danger: true })
    if (!ok) return
    try {
      await cancelTask(id)
      toast.success('Task cancelled')
    } catch (e) {
      toast.error(e instanceof ApiError && e.status === 409 ? 'Only queued tasks can be cancelled.' : messageOf(e).join(' '))
    }
    await refresh()
  }

  const from = total === 0 ? 0 : skip + 1
  const to = Math.min(skip + PAGE_SIZE, total)
  const filtered = Boolean(q || f.verdict || f.status || f.org || f.mine || f.from || f.to)
  const onSort = (field: string, dir: 'asc' | 'desc') => setF({ sort: field, dir, page: 0 })
  const sortProps = { sort: f.sort, dir: f.dir as 'asc' | 'desc', onSort }
  const clearFilters = () => {
    setSearch('')
    setF({ q: '', verdict: '', status: '', org: '', mine: false, from: '', to: '', page: 0 })
  }

  return (
    <section>
      <h1>Tasks</h1>
      <div className={styles.filters}>
        <div className={styles.field}>
          <label htmlFor="filterSearch">Search</label>
          <input id="filterSearch" type="text" data-search aria-label="Search tasks" placeholder="Hash, file name or URL" value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
        <div className={styles.field}>
          <label htmlFor="filterVerdict">Verdict</label>
          <select id="filterVerdict" value={f.verdict} onChange={(e) => setF({ verdict: e.target.value, page: 0 })}>
            <option value="">All</option>
            {VERDICTS.map((v) => <option key={v} value={v}>{v}</option>)}
          </select>
        </div>
        <div className={styles.field}>
          <label htmlFor="filterOrg">Organization</label>
          <select id="filterOrg" value={f.org} onChange={(e) => setF({ org: e.target.value, page: 0 })}>
            <option value="">All</option>
            {orgs.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
          </select>
        </div>
        <div className={styles.field}>
          <label htmlFor="filterStatus">Status</label>
          <select id="filterStatus" value={f.status} onChange={(e) => setF({ status: e.target.value, page: 0 })}>
            <option value="">All</option>
            {RUN_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </div>
        <div className={styles.field}>
          <label htmlFor="filterFrom">From</label>
          <input id="filterFrom" type="date" value={f.from} max={f.to || undefined} onChange={(e) => setF({ from: e.target.value, page: 0 })} />
        </div>
        <div className={styles.field}>
          <label htmlFor="filterTo">To</label>
          <input id="filterTo" type="date" value={f.to} min={f.from || undefined} onChange={(e) => setF({ to: e.target.value, page: 0 })} />
        </div>
        <label className={styles.inline}>
          <input type="checkbox" checked={f.mine} onChange={(e) => setF({ mine: e.target.checked, page: 0 })} /> My tasks
        </label>
        <Link to="/submit">Submit a sample</Link>
      </div>

      {error && <ErrorBanner message={error} onRetry={() => void refresh()} />}
      {!loaded && !error && <Skeleton rows={5} />}

      {loaded && items.length === 0 && !error && (
        filtered ? (
          <EmptyState title="No tasks match these filters" action={<button type="button" className={styles.secondary} onClick={clearFilters}>Clear filters</button>} />
        ) : (
          <EmptyState title="Submit your first sample" body="Tasks you submit appear here with their status and verdict." action={<Link to="/submit">Submit a sample</Link>} />
        )
      )}
      {items.length > 0 && <div className={styles.tableScroll}><table className={styles.table}>
        <thead>
          <tr>
            <th>Sample</th><th>Organization</th><th>Submitted by</th><th>Status</th>
            <SortHeader field="verdict" label="Verdict" {...sortProps} />
            <SortHeader field="duration" label="Duration" {...sortProps} />
            <th>Mode</th>
            <SortHeader field="created" label="Created" {...sortProps} />
            <th />
          </tr>
        </thead>
        <tbody>
          {items.map((t) => (
            <tr key={t.id}>
              <td className={styles.mono}>
                <Link to={`/tasks/${t.id}`}>{target(t)}</Link>
                {(t.url || (!t.originalFileName && t.sampleSha256)) && <CopyButton value={t.url ?? t.sampleSha256 ?? ''} label={t.url ? 'Copy URL' : 'Copy SHA-256'} />}
              </td>
              <td>{t.orgName}</td>
              <td>{t.submittedByName ? safeText(t.submittedByName) : ''}</td>
              <td>
                <StatusBadge status={t.status} />
                {t.status === 'Queued' && t.queuePosition > 0 && <span className={styles.hint}> #{t.queuePosition} in queue</span>}
              </td>
              <td><VerdictBadge verdict={t.verdict} /></td>
              <td>{t.ranSec != null ? <Duration seconds={t.ranSec} /> : <span className={styles.hint}>-</span>}</td>
              <td>{t.networkMode}</td>
              <td><RelativeTime iso={t.createdAt} /></td>
              <td>
                {t.status === 'Queued' && (
                  <button type="button" className={styles.secondary} onClick={() => void onCancel(t.id)}>Cancel</button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table></div>}

      <div className={styles.pager}>
        <button type="button" className={styles.secondary} disabled={f.page === 0} onClick={() => setF({ page: Math.max(0, f.page - 1) })}>Previous</button>
        <span>{from}–{to} of {total}</span>
        <button type="button" className={styles.secondary} disabled={skip + PAGE_SIZE >= total} onClick={() => setF({ page: f.page + 1 })}>Next</button>
      </div>
    </section>
  )
}
