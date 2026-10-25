import { useCallback, useEffect, useRef, useState, type MouseEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError, getAdminTemplate } from '../api/client'
import type { MeDto, TemplateDetailDto, TemplateJobDto, TemplateJobState, TemplateVersionDto, TemplateVersionRole } from '../api/types'
import badge from '../components/StatusBadge.module.css'
import { CopyButton } from '../kit/CopyButton'
import { EmptyState } from '../kit/EmptyState'
import { ErrorBanner } from '../kit/ErrorBanner'
import { Duration, FileSize, RelativeTime } from '../kit/RelativeTime'
import { Skeleton } from '../kit/Skeleton'
import { messageOf } from '../util/apiMessage'
import { safeText } from '../util/safeText'
import styles from './forms.module.css'
import page from './TemplateDetailPage.module.css'
import JobDialog from './templates/JobDialog'
import { useTemplateActions } from './templates/useTemplateActions'
import { ALREADY_DEFAULT, isActiveJob, JOB_POLL_MS, jobBlockReason, NO_VERSION, officeLabel, rollbackDiskReason, rollbackTarget, windowsLabel } from './templates/versions'

const ROLE_LABEL: Record<TemplateVersionRole, string> = {
  Current: 'Current',
  Previous: 'Kept',
  RollbackTarget: 'Rollback target',
  Pending: 'Not published',
  Retired: 'Retired',
  Deleted: 'Deleted',
}

const NO_PREVIOUS = 'No previous version to roll back to.'
const DEFAULT_NOT_DELETABLE = 'The default template cannot be deleted.'
const HAS_VERSIONS = 'Only a template with no versions can be deleted.'

/** Job states in the analysis badge colours: queued, active, done, failed, cancelled. */
function jobStateClass(s: TemplateJobState): string {
  if (s === 'Queued') return badge.queued
  if (s === 'Published' || s === 'Sealed') return badge.completed
  if (s === 'Failed') return badge.failed
  if (s === 'Cancelled') return badge.cancelled
  return badge.active
}

function JobStateBadge({ state }: { state: TemplateJobState }) {
  return <span className={`${badge.badge} ${jobStateClass(state)}`}>{state}</span>
}

/** Created to ended; ticks every second while the job has not ended. */
function JobDuration({ job }: { job: TemplateJobDto }) {
  const ended = job.endedAt ? Date.parse(job.endedAt) : null
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (ended !== null) return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [ended])
  return <Duration seconds={((ended ?? now) - Date.parse(job.createdAt)) / 1000} />
}

function JobsTable({ jobs }: { jobs: TemplateJobDto[] }) {
  if (jobs.length === 0) return <EmptyState title="No jobs yet" />
  return (
    <div className={styles.tableScroll}>
      <table className={styles.table} aria-label="Jobs">
        <thead><tr><th>Job</th><th>Versions</th><th>State</th><th>Started</th><th>Duration</th><th>Requested by</th></tr></thead>
        <tbody>
          {jobs.map((j) => (
            <tr key={j.id}>
              <td><Link to={`/admin/template-jobs/${j.id}`}>{j.kind}</Link></td>
              <td className={styles.mono}>{`${j.baseVersionNumber != null ? `v${j.baseVersionNumber}` : 'new'} → ${j.resultVersionNumber != null ? `v${j.resultVersionNumber}` : ''}`}</td>
              <td>
                <JobStateBadge state={j.state} />
                {j.state === 'Failed' && j.failureReason && <span className={page.reason}>{safeText(j.failureReason)}</span>}
              </td>
              <td><RelativeTime iso={j.createdAt} /></td>
              <td className={styles.mono}><JobDuration job={j} /></td>
              <td>{j.requestedByName ? safeText(j.requestedByName) : '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Hash({ label, value }: { label: string; value: string | null | undefined }) {
  return (
    <div className={page.field}>
      <dt>{label}</dt>
      <dd className={page.hash}>
        {value ? <><code className={styles.mono}>{value}</code><CopyButton value={value} label={`Copy ${label}`} /></> : <span className={styles.hint}>Unknown</span>}
      </dd>
    </div>
  )
}

function VersionItem({ v, busy, onPublish, onDiscard }: {
  v: TemplateVersionDto
  busy: boolean
  onPublish: (v: TemplateVersionDto) => void
  onDiscard: (v: TemplateVersionDto) => void
}) {
  const windows = windowsLabel(v)
  return (
    <li className={page.version} aria-label={`v${v.number}`}>
      <div className={page.versionHead}>
        <span className={page.number}>v{v.number}</span>
        <span className={`${page.role} ${v.role === 'Current' ? page.roleCurrent : ''}`}>{ROLE_LABEL[v.role]}</span>
        <span>{v.origin}</span>
        {v.parentNumber != null && <span className={styles.hint}>{`from v${v.parentNumber}`}</span>}
        {v.status !== 'Deleted' && !v.diskPresent && <span className={page.warn}>Disk missing</span>}
        <span className={page.spacer} />
        <span className={styles.hint}>{v.taskCount === 1 ? '1 task' : `${v.taskCount} tasks`}</span>
      </div>
      <p className={page.line}>
        <span>{v.sealedBy ? `Sealed by ${safeText(v.sealedBy)} ` : 'Sealed '}</span>
        <RelativeTime iso={v.sealedAt} />
        {v.sizeBytes != null && <> · <FileSize bytes={v.sizeBytes} /></>}
      </p>
      <p className={page.line}>
        {windows && <><span>{safeText(windows)}</span> · </>}
        <span>{officeLabel(v.officePresent)}</span>
        {v.agentVersion && <> · <span>{`agent ${safeText(v.agentVersion)}`}</span></>}
      </p>
      {v.note && <p className={page.note}>{safeText(v.note)}</p>}
      <dl className={page.fields}>
        {v.sourceMedia.length > 0 && (
          <div className={page.field}>
            <dt>Source media</dt>
            <dd>
              <ul className={page.media}>
                {v.sourceMedia.map((m) => (
                  <li key={m.id}>{m.name === '(deleted)' ? <span className={styles.hint}>(deleted)</span> : <Link to="/admin/media">{safeText(m.name)}</Link>}</li>
                ))}
              </ul>
            </dd>
          </div>
        )}
        <Hash label="Sysmon config SHA-256" value={v.sysmonConfigSha256} />
        <Hash label="mitm CA SHA-256" value={v.mitmCaSha256} />
      </dl>
      {v.role === 'Pending' && (
        <div className={page.rowActions}>
          <button type="button" className={styles.secondary} disabled={busy} onClick={() => onPublish(v)}>{`Publish v${v.number}`}</button>
          <button type="button" className={styles.secondary} disabled={busy} onClick={() => onDiscard(v)}>{`Discard v${v.number}`}</button>
        </div>
      )}
    </li>
  )
}

/** A header action; a reason disables it and is both its tooltip and its accessible description. */
function ActionButton({ label, reason, describedBy, busy, onClick }: {
  label: string
  reason: string | undefined
  describedBy: string | undefined
  busy: boolean
  onClick: (e: MouseEvent<HTMLButtonElement>) => void
}) {
  return <button type="button" className={styles.secondary} disabled={busy || !!reason} title={reason} aria-describedby={describedBy} onClick={onClick}>{label}</button>
}

function TemplateDetail({ detail, onChanged }: { detail: TemplateDetailDto; onChanged: () => Promise<void> }) {
  const { template: t, jobs } = detail
  const navigate = useNavigate()
  const { busy, publish, makeDefault, toggleEnabled, discard, remove, rollBack } = useTemplateActions(t, onChanged, { onDeleted: () => navigate('/admin/templates') })
  const [dialog, setDialog] = useState<'Edit' | 'Refresh' | null>(null)
  const opener = useRef<HTMLElement | null>(null)

  const activeJob = jobs.find(isActiveJob)
  const target = rollbackTarget(t)
  const versions = [...t.versions].sort((a, b) => b.number - a.number)

  const jobReason = t.current ? jobBlockReason(activeJob) : NO_VERSION
  const rollbackReason = target ? rollbackDiskReason(target) : NO_PREVIOUS
  const defaultReason = t.isDefault ? ALREADY_DEFAULT : undefined
  const deleteReason = t.isDefault ? DEFAULT_NOT_DELETABLE : t.versions.length > 0 ? HAS_VERSIONS : jobBlockReason(activeJob)
  const reasons = [...new Set([jobReason, rollbackReason, defaultReason, deleteReason].filter((r): r is string => !!r))]
  const reasonId = (r: string | undefined) => (r ? `reason-${reasons.indexOf(r)}` : undefined)

  function openDialog(kind: 'Edit' | 'Refresh', trigger: HTMLElement) {
    opener.current = trigger
    setDialog(kind)
  }

  function closeDialog() {
    setDialog(null)
    opener.current?.focus() // focus returns to the trigger
  }

  return (
    <section aria-labelledby="template-title">
      <p className={page.crumb}><Link to="/admin/templates">Templates</Link></p>
      <div className={page.head}>
        <h1 id="template-title">{safeText(t.displayName)}</h1>
        <code className={page.id}>{t.id}</code>
        {t.isDefault && <span className={page.badge}>Default</span>}
        {!t.enabled && <span className={page.badge}>Disabled</span>}
      </div>
      {t.description && <p className={styles.hint}>{safeText(t.description)}</p>}
      <p className={styles.hint}>{t.tasksLast30Days === 1 ? '1 task in the last 30 days' : `${t.tasksLast30Days} tasks in the last 30 days`}</p>
      <div className={page.actions}>
        <ActionButton label="Edit" reason={jobReason} describedBy={reasonId(jobReason)} busy={busy} onClick={(e) => openDialog('Edit', e.currentTarget)} />
        <ActionButton label="Refresh components" reason={jobReason} describedBy={reasonId(jobReason)} busy={busy} onClick={(e) => openDialog('Refresh', e.currentTarget)} />
        <ActionButton label="Roll back" reason={rollbackReason} describedBy={reasonId(rollbackReason)} busy={busy} onClick={() => void rollBack()} />
        <ActionButton label="Set as default" reason={defaultReason} describedBy={reasonId(defaultReason)} busy={busy} onClick={() => void makeDefault()} />
        <ActionButton label={t.enabled ? 'Disable' : 'Enable'} reason={undefined} describedBy={undefined} busy={busy} onClick={() => void toggleEnabled()} />
        <ActionButton label="Delete" reason={deleteReason} describedBy={reasonId(deleteReason)} busy={busy} onClick={() => void remove()} />
      </div>
      {reasons.length > 0 && (
        <ul className={page.reasons}>
          {reasons.map((r, i) => <li key={r} id={`reason-${i}`}>{r}</li>)}
        </ul>
      )}
      {activeJob && <p className={page.line}>{jobBlockReason(activeJob)} <Link to={`/admin/template-jobs/${activeJob.id}`}>Open the job</Link></p>}
      {dialog && <JobDialog kind={dialog} template={t} onClose={closeDialog} onStarted={(job) => navigate(`/admin/template-jobs/${job.id}`)} />}

      <h2 className={page.subhead}>Versions</h2>
      {versions.length === 0 ? <EmptyState title="No versions yet" /> : (
        <ol className={page.timeline} aria-label="Versions">
          {versions.map((v) => <VersionItem key={v.id} v={v} busy={busy} onPublish={(x) => void publish(x)} onDiscard={(x) => void discard(x)} />)}
        </ol>
      )}

      <h2 className={page.subhead}>Jobs</h2>
      <JobsTable jobs={jobs} />
    </section>
  )
}

/** Admin-only detail of one template: actions, the version timeline and its jobs. */
export default function TemplateDetailPage({ me }: { me: MeDto | null }) {
  const { id = '' } = useParams()
  const [held, setHeld] = useState<{ id: string; detail: TemplateDetailDto } | null>(null)
  const detail = held && held.id === id ? held.detail : null // another template's detail is never shown
  const [loadError, setLoadError] = useState<string | null>(null)
  const [notFound, setNotFound] = useState(false)
  const isAdmin = me?.role === 'Admin'
  const seq = useRef(0) // the newest load wins: an older answer (a slow poll) never overwrites a newer one
  const pending = useRef(0) // loads in flight

  const load = useCallback(async () => {
    const mine = ++seq.current
    pending.current++
    try {
      const d = await getAdminTemplate(id)
      if (mine !== seq.current) return
      setHeld({ id, detail: d })
      setLoadError(null)
      setNotFound(false)
    } catch (e) {
      if (mine !== seq.current) return
      if (e instanceof ApiError && e.status === 404) setNotFound(true)
      else setLoadError(messageOf(e)[0]) // the last loaded detail stays on screen
    } finally {
      pending.current--
    }
  }, [id])

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- a new id starts clean, then fetches
    setLoadError(null)
    setNotFound(false)
    if (isAdmin) void load()
  }, [isAdmin, load])

  // While a job runs, poll so its state and the versions it produces show up.
  const polling = !!detail?.jobs.some(isActiveJob)
  useEffect(() => {
    if (!polling) return
    const timer = setInterval(() => {
      if (document.hidden || pending.current > 0) return // skip while the tab is hidden or a request is still running
      void load()
    }, JOB_POLL_MS)
    return () => clearInterval(timer)
  }, [polling, load])

  if (!me) return <p>Loading…</p>
  if (!isAdmin) return <p role="alert">Administrator access is required to manage sandbox templates.</p>
  if (notFound) {
    return <EmptyState title="Template not found" body="It may have been deleted." action={<Link to="/admin/templates">Back to templates</Link>} />
  }

  return (
    <>
      {loadError && (
        <ErrorBanner
          message={detail ? `Could not refresh the template (${loadError}). Showing the last loaded versions.` : loadError}
          onRetry={() => void load()}
        />
      )}
      {!detail && !loadError && <Skeleton rows={6} />}
      {detail && <TemplateDetail detail={detail} onChanged={load} />}
    </>
  )
}
