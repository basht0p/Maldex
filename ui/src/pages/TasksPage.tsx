import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { ApiError, cancelTask, listOrgs, listTasks } from '../api/client'
import { RUN_STATUSES, type OrgDto, type RunStatus, type TaskDto } from '../api/types'
import { StatusBadge, VerdictBadge } from '../components/StatusBadge'
import { safeText } from '../util/safeText'
import styles from './forms.module.css'

const PAGE_SIZE = 25
const REFRESH_MS = 5000

function target(t: TaskDto): string {
  if (t.url) return safeText(t.url)
  if (t.originalFileName) return safeText(t.originalFileName)
  return t.sampleSha256 ? t.sampleSha256 : '(unnamed)'
}

export default function TasksPage() {
  const [orgs, setOrgs] = useState<OrgDto[]>([])
  const [orgId, setOrgId] = useState('')
  const [status, setStatus] = useState<RunStatus | ''>('')
  const [skip, setSkip] = useState(0)
  const [items, setItems] = useState<TaskDto[]>([])
  const [total, setTotal] = useState(0)
  const [loaded, setLoaded] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      const page = await listTasks({ orgId: orgId || undefined, status: status || undefined, skip, take: PAGE_SIZE })
      setItems(page.items)
      setTotal(page.total)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load tasks.')
    } finally {
      setLoaded(true)
    }
  }, [orgId, status, skip])

  useEffect(() => {
    listOrgs().then(setOrgs, () => undefined)
  }, [])

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- initial fetch + polling
    void refresh()
    const timer = setInterval(() => void refresh(), REFRESH_MS)
    return () => clearInterval(timer)
  }, [refresh])

  async function onCancel(id: string) {
    setActionError(null)
    try {
      await cancelTask(id)
      setActionError(null)
    } catch (e) {
      setActionError(e instanceof ApiError && e.status === 409 ? 'Only queued tasks can be cancelled.' : e instanceof Error ? e.message : 'Cancel failed.')
    }
    await refresh()
  }

  const from = total === 0 ? 0 : skip + 1
  const to = Math.min(skip + PAGE_SIZE, total)

  return (
    <section>
      <h1>Tasks</h1>
      <div className={styles.filters}>
        <div className={styles.field}>
          <label htmlFor="filterOrg">Organization</label>
          <select id="filterOrg" value={orgId} onChange={(e) => { setOrgId(e.target.value); setSkip(0); setActionError(null) }}>
            <option value="">All</option>
            {orgs.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
          </select>
        </div>
        <div className={styles.field}>
          <label htmlFor="filterStatus">Status</label>
          <select id="filterStatus" value={status} onChange={(e) => { setStatus(e.target.value as RunStatus | ''); setSkip(0); setActionError(null) }}>
            <option value="">All</option>
            {RUN_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </div>
        <Link to="/submit">Submit a sample</Link>
      </div>

      {(actionError ?? error) && <p className={styles.error} role="alert">{actionError ?? error}</p>}

      <table className={styles.table}>
        <thead>
          <tr>
            <th>Sample</th><th>Organization</th><th>Status</th><th>Verdict</th><th>Mode</th><th>Created</th><th />
          </tr>
        </thead>
        <tbody>
          {items.map((t) => (
            <tr key={t.id}>
              <td className={styles.mono}><Link to={`/tasks/${t.id}`}>{target(t)}</Link></td>
              <td>{t.orgName}</td>
              <td>
                <StatusBadge status={t.status} />
                {t.status === 'Queued' && t.queuePosition > 0 && <span className={styles.hint}> #{t.queuePosition} in queue</span>}
              </td>
              <td><VerdictBadge verdict={t.verdict} /></td>
              <td>{t.networkMode}</td>
              <td>{new Date(t.createdAt).toLocaleString()}</td>
              <td>
                {t.status === 'Queued' && (
                  <button type="button" className={styles.secondary} onClick={() => void onCancel(t.id)}>Cancel</button>
                )}
              </td>
            </tr>
          ))}
          {loaded && items.length === 0 && (
            <tr><td colSpan={7} className={styles.hint}>No tasks yet.</td></tr>
          )}
        </tbody>
      </table>

      <div className={styles.pager}>
        <button type="button" className={styles.secondary} disabled={skip === 0} onClick={() => { setSkip(Math.max(0, skip - PAGE_SIZE)); setActionError(null) }}>Previous</button>
        <span>{from}–{to} of {total}</span>
        <button type="button" className={styles.secondary} disabled={skip + PAGE_SIZE >= total} onClick={() => { setSkip(skip + PAGE_SIZE); setActionError(null) }}>Next</button>
      </div>
    </section>
  )
}
