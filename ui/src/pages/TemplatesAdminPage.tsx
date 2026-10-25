import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { listAdminTemplates, listTemplateJobs, setDefaultTemplate, setTemplateEnabled } from '../api/client'
import type { AdminTemplateDto, MeDto, TemplateJobDto } from '../api/types'
import { ErrorBanner } from '../kit/ErrorBanner'
import { RelativeTime } from '../kit/RelativeTime'
import { Skeleton } from '../kit/Skeleton'
import { messageOf } from '../util/apiMessage'
import styles from './forms.module.css'
import MediaLibrary from './MediaLibrary'
import NewTemplateDialog from './NewTemplateDialog'
import { JobDialog } from './templates/JobDialog'
import { useTemplateActions } from './templates/useTemplateActions'
import { isActiveJob, JOB_POLL_MS, jobBlockReason, keptVersions, NO_VERSION, officeLabel, rollbackDiskReason, rollbackTarget, versionSummary } from './templates/versions'
import page from './TemplatesAdminPage.module.css'

function JobsList({ jobs }: { jobs: TemplateJobDto[] }) {
  if (jobs.length === 0) return <p className={styles.hint}>No jobs yet</p>
  return (
    <div className={styles.tableScroll}>
    <table className={styles.table} aria-label="Jobs">
      <thead><tr><th>Job</th><th>Base</th><th>Result</th><th>State</th><th>Started</th></tr></thead>
      <tbody>
        {jobs.map((j) => (
          <tr key={j.id}>
            <td><Link to={`/admin/template-jobs/${j.id}`}>{j.kind}</Link></td>
            <td className={styles.mono}>{j.baseVersionNumber != null ? `v${j.baseVersionNumber}` : 'new'}</td>
            <td className={styles.mono}>{j.resultVersionNumber != null ? `v${j.resultVersionNumber}` : ''}</td>
            <td>{j.state}{j.state === 'Failed' && j.failureReason ? `: ${j.failureReason}` : ''}</td>
            <td><RelativeTime iso={j.createdAt} /></td>
          </tr>
        ))}
      </tbody>
    </table>
    </div>
  )
}

function TemplateCard({ template: t, onChanged }: { template: AdminTemplateDto; onChanged: () => Promise<void> }) {
  const { busy, run, publish, discard, remove, rollBack } = useTemplateActions(t, onChanged)
  const kept = keptVersions(t)
  const previous = rollbackTarget(t)
  const pending = t.versions.filter((v) => v.role === 'Pending')
  const previousDiskReason = rollbackDiskReason(previous)
  const headingId = `template-${t.id}`
  const navigate = useNavigate()
  const [jobs, setJobs] = useState<TemplateJobDto[]>([])
  const [jobsLoaded, setJobsLoaded] = useState(false) // Delete waits for it: an active Build must not look absent
  const [dialog, setDialog] = useState<'Edit' | 'Refresh' | null>(null)
  const opener = useRef<HTMLElement | null>(null)
  const activeJob = jobs.find(isActiveJob)
  const canDelete = jobsLoaded && t.versions.length === 0 && !activeJob && !t.isDefault
  const noVersion = !t.current
  const noVersionId = `${headingId}-no-version`
  const jobBlock = jobBlockReason(activeJob)

  useEffect(() => {
    let cancelled = false
    listTemplateJobs(t.id).then((list) => { if (!cancelled) { setJobs(list); setJobsLoaded(true) } }, () => undefined) // best effort: the 409 on start is the guard
    return () => { cancelled = true }
  }, [t])

  function openDialog(kind: 'Edit' | 'Refresh', trigger: HTMLElement) {
    opener.current = trigger
    setDialog(kind)
  }

  function closeDialog() {
    setDialog(null)
    opener.current?.focus() // focus returns to the trigger
  }

  const polling = !!activeJob
  useEffect(() => {
    if (!polling) return
    let cancelled = false
    const timer = setInterval(() => {
      listTemplateJobs(t.id).then((list) => {
        if (cancelled) return
        setJobs(list)
        // the job reached a terminal state: reload the template so its versions (and Edit/Refresh) are current
        if (!list.some(isActiveJob)) void onChanged()
      }, () => undefined)
    }, JOB_POLL_MS)
    return () => { cancelled = true; clearInterval(timer) }
  }, [polling, t.id, onChanged])

  return (
    <section className={page.card} aria-labelledby={headingId}>
      <div className={page.head}>
        <h2 id={headingId}>{t.displayName}</h2>
        <code className={page.id}>{t.id}</code>
        {t.isDefault && <span className={page.badge}>Default</span>}
        <span className={page.spacer} />
        <label className={page.toggle}>
          <input type="checkbox" checked={t.enabled} disabled={busy} onChange={(e) => void run(() => setTemplateEnabled(t.id, e.target.checked), e.target.checked ? 'Template enabled' : 'Template disabled')} />
          Enabled
        </label>
        <button type="button" className={styles.secondary} disabled={busy || t.isDefault} onClick={() => void run(() => setDefaultTemplate(t.id), 'Set as default')}>Set as default</button>
        <button type="button" className={styles.secondary} disabled={busy || !!activeJob || noVersion} title={noVersion ? NO_VERSION : jobBlock} aria-describedby={noVersion ? noVersionId : undefined} onClick={(e) => openDialog('Edit', e.currentTarget)}>Edit</button>
        <button type="button" className={styles.secondary} disabled={busy || !!activeJob || noVersion} title={noVersion ? NO_VERSION : jobBlock} aria-describedby={noVersion ? noVersionId : undefined} onClick={(e) => openDialog('Refresh', e.currentTarget)}>Refresh components</button>
        {noVersion && <span id={noVersionId} hidden>{NO_VERSION}</span>}
        <button type="button" className={styles.secondary} disabled={busy || !previous || !!previousDiskReason} title={previousDiskReason} onClick={() => void rollBack()}>Roll back</button>
        {canDelete && <button type="button" className={styles.secondary} disabled={busy} onClick={() => void remove()}>Delete</button>}
      </div>
      {t.description && <p className={styles.hint}>{t.description}</p>}
      {jobBlock && activeJob && <p className={page.summary}>{jobBlock} <Link to={`/admin/template-jobs/${activeJob.id}`}>Open the job</Link></p>}
      {dialog && <JobDialog kind={dialog} template={t} onClose={closeDialog} onStarted={(job) => navigate(`/admin/template-jobs/${job.id}`)} />}
      <p className={page.summary}>
        {t.current ? <>
            <span className={styles.mono}>{`Current: ${versionSummary(t.current)}`}</span>{' '}
            <span className={styles.hint}>{officeLabel(t.current.officePresent)}</span>
          </> : <span className={styles.hint}>{t.versions.length === 0 ? 'No version yet' : 'No current version'}</span>}
      </p>
      <p className={page.summary}>
        {kept.length > 0 ? <span className={styles.mono}>{`Previous kept: ${kept.map((v) => `v${v.number}`).join(', ')}`}</span> : <span className={styles.hint}>No previous version kept</span>}
      </p>
      {pending.length > 0 && (
        <p className={page.summary}>
          <span className={styles.hint}>{`Not yet published: ${pending.map((v) => `v${v.number}`).join(', ')}`}</span>
        </p>
      )}
      <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead><tr><th>Version</th><th>Origin</th><th>Status</th><th>Disk</th><th>Actions</th></tr></thead>
        <tbody>
          {t.versions.map((v) => (
            <tr key={v.id}>
              <td className={styles.mono}>v{v.number}{v.isCurrent ? ' (current)' : ''}</td>
              <td>{v.origin}</td>
              <td>{v.status}</td>
              <td>{v.status !== 'Deleted' && !v.diskPresent && <span className={page.warn}>Disk missing</span>}</td>
              <td>
                {v.role === 'Pending' && (
                  <span className={page.rowActions}>
                    <button type="button" className={styles.secondary} disabled={busy} onClick={() => void publish(v)}>{`Publish v${v.number}`}</button>
                    <button type="button" className={styles.secondary} disabled={busy} onClick={() => void discard(v)}>{`Discard v${v.number}`}</button>
                  </span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>
      <h3 className={page.subhead}>Jobs</h3>
      <JobsList jobs={jobs} />
    </section>
  )
}

type Tab = 'templates' | 'media'

/** Admin-only. Non-admins see a notice; the API enforces the same rule server-side. */
export default function TemplatesAdminPage({ me }: { me: MeDto | null }) {
  const [templates, setTemplates] = useState<AdminTemplateDto[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)
  const [tab, setTab] = useState<Tab>('templates')
  const [mediaOpened, setMediaOpened] = useState(false) // the library stays mounted once opened, so an upload survives a tab switch
  const [building, setBuilding] = useState(false)
  const opener = useRef<HTMLElement | null>(null)
  const navigate = useNavigate()
  const isAdmin = me?.role === 'Admin'

  const refresh = useCallback(async () => {
    try {
      setTemplates(await listAdminTemplates())
      setLoadError(null)
    } catch (e) {
      setLoadError(messageOf(e)[0]) // the last loaded templates stay on screen
    } finally {
      setLoaded(true)
    }
  }, [])

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- initial fetch
    if (isAdmin) void refresh()
  }, [isAdmin, refresh])

  if (!me) return <p>Loading…</p>
  if (!isAdmin) return <p role="alert">Administrator access is required to manage sandbox templates.</p>

  function choose(next: Tab) {
    setTab(next)
    if (next === 'media') setMediaOpened(true)
  }

  function closeBuild() {
    setBuilding(false)
    opener.current?.focus() // focus returns to the trigger
  }

  return (
    <section>
      <h1>Sandbox Templates</h1>
      <div role="tablist" aria-label="Sandbox templates" className={page.tabs}>
        <button type="button" role="tab" id="tab-templates" aria-selected={tab === 'templates'} aria-controls="panel-templates" className={page.tab} onClick={() => choose('templates')}>Templates</button>
        <button type="button" role="tab" id="tab-media" aria-selected={tab === 'media'} aria-controls="panel-media" className={page.tab} onClick={() => choose('media')}>Media</button>
      </div>
      <div role="tabpanel" id="panel-templates" aria-labelledby="tab-templates" hidden={tab !== 'templates'}>
        <div className={page.toolbar}>
          <button type="button" className={styles.primary} onClick={(e) => { opener.current = e.currentTarget; setBuilding(true) }}>New template</button>
        </div>
        {building && <NewTemplateDialog templates={templates} onClose={closeBuild} onStarted={(job) => navigate(`/admin/template-jobs/${job.id}`)} />}
        {loadError && (
          <ErrorBanner
            message={templates.length > 0 ? `Could not refresh the templates (${loadError}). Showing the last loaded versions.` : loadError}
            onRetry={() => void refresh()}
          />
        )}
        {!loaded && !loadError && <Skeleton rows={4} />}
        {templates.map((t) => <TemplateCard key={t.id} template={t} onChanged={refresh} />)}
      </div>
      {mediaOpened && (
        <div role="tabpanel" id="panel-media" aria-labelledby="tab-media" hidden={tab !== 'media'}>
          <MediaLibrary />
        </div>
      )}
    </section>
  )
}
