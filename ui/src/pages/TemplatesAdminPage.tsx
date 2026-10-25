import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import {
  deleteTemplate,
  discardTemplateVersion,
  listAdminTemplates,
  listTemplateJobs,
  publishTemplateVersion,
  rollBackTemplate,
  setDefaultTemplate,
  setTemplateEnabled,
  startTemplateEdit,
  startTemplateRefresh,
} from '../api/client'
import type { AdminTemplateDto, MeDto, TemplateJobDto, TemplateJobNetwork, TemplateVersionDto } from '../api/types'
import { messageOf } from '../util/apiMessage'
import { useDialogFocus } from './dialogFocus'
import styles from './forms.module.css'
import MediaLibrary from './MediaLibrary'
import NewTemplateDialog from './NewTemplateDialog'
import page from './TemplatesAdminPage.module.css'

/** e.g. `v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)`; mirrors the server's task provenance line. */
function versionSummary(v: TemplateVersionDto): string {
  const parts: string[] = []
  const windows = [v.windowsEdition, v.windowsBuild].filter((s) => s && s.trim()).join(' ')
  if (windows) parts.push(windows)
  if (v.agentVersion) parts.push(`agent ${v.agentVersion}`)
  parts.push(`sealed ${v.sealedAt.slice(0, 10)}${v.sealedBy ? ` by ${v.sealedBy}` : ''}`)
  return `v${v.number} (${parts.join(', ')})`
}

/** The versions the server keeps for rollback, rollback target first (its `role` decides; a disk may still be missing). */
function keptVersions(t: AdminTemplateDto): TemplateVersionDto[] {
  const target = t.versions.filter((v) => v.role === 'RollbackTarget')
  return [...target, ...t.versions.filter((v) => v.role === 'Previous')]
}

function officeLabel(present: boolean | null | undefined): string {
  return present == null ? 'Office unknown' : present ? 'Office ✓' : 'Office ✗'
}

const ACTIVE_JOB_STATES = ['Queued', 'Preparing', 'Installing', 'Provisioning', 'Configuring', 'Sealing', 'Verifying']
/** While a job of the template is active, its job list is polled at this interval to notice the terminal state. */
const JOB_POLL_MS = 5000

/** Start dialog for an Edit (with the network choice) or a Refresh (confirm only). Errors, such as a 409 reason, show inside it. */
function JobDialog({ kind, template, onClose, onStarted }: {
  kind: 'Edit' | 'Refresh'
  template: AdminTemplateDto
  onClose: () => void
  onStarted: (job: TemplateJobDto) => void
}) {
  const [network, setNetwork] = useState<TemplateJobNetwork>('None')
  const [publish, setPublish] = useState(true)
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const titleId = `job-dialog-${template.id}`
  const dialogRef = useRef<HTMLDivElement>(null)

  const onKeyDown = useDialogFocus(dialogRef, busy, onClose)

  async function start() {
    setBusy(true)
    try {
      onStarted(kind === 'Edit' ? await startTemplateEdit(template.id, network, publish) : await startTemplateRefresh(template.id, publish))
    } catch (e) {
      setErrors(messageOf(e))
      setBusy(false)
    }
  }

  return (
    <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby={titleId} className={page.dialog} onKeyDown={onKeyDown}>
      <h3 id={titleId}>{kind === 'Edit' ? `Edit ${template.displayName}` : `Refresh components of ${template.displayName}`}</h3>
      {kind === 'Edit' ? (
        <fieldset className={page.fieldset}>
          <legend className={styles.legend}>Network</legend>
          <label className={styles.inline}><input type="radio" name={`network-${template.id}`} checked={network === 'None'} onChange={() => setNetwork('None')} />None</label>
          <label className={styles.inline}><input type="radio" name={`network-${template.id}`} checked={network === 'Direct'} onChange={() => setNetwork('Direct')} />Direct: internet on</label>
          {network === 'Direct' && <p className={page.warn}>The working copy can reach the internet while you edit it.</p>}
        </fieldset>
      ) : (
        <p>Starts the working copy, updates Windows and the Office components unattended, then seals and verifies the result. No one works in the guest.</p>
      )}
      <label className={styles.inline}><input type="checkbox" checked={publish} onChange={(e) => setPublish(e.target.checked)} />Publish when done</label>
      {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
      <div className={page.dialogActions}>
        <button type="button" className={styles.primary} disabled={busy} onClick={() => void start()}>{kind === 'Edit' ? 'Start edit' : 'Start refresh'}</button>
        <button type="button" className={styles.secondary} disabled={busy} onClick={onClose}>Cancel</button>
      </div>
    </div>
  )
}

function JobsList({ jobs }: { jobs: TemplateJobDto[] }) {
  if (jobs.length === 0) return <p className={styles.hint}>No jobs yet</p>
  return (
    <table className={styles.table} aria-label="Jobs">
      <thead><tr><th>Job</th><th>Base</th><th>Result</th><th>State</th><th>Started</th></tr></thead>
      <tbody>
        {jobs.map((j) => (
          <tr key={j.id}>
            <td><Link to={`/admin/template-jobs/${j.id}`}>{j.kind}</Link></td>
            <td className={styles.mono}>{j.baseVersionNumber != null ? `v${j.baseVersionNumber}` : 'new'}</td>
            <td className={styles.mono}>{j.resultVersionNumber != null ? `v${j.resultVersionNumber}` : ''}</td>
            <td>{j.state}{j.state === 'Failed' && j.failureReason ? `: ${j.failureReason}` : ''}</td>
            <td>{j.createdAt.slice(0, 16).replace('T', ' ')}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function TemplateCard({ template: t, onChanged }: { template: AdminTemplateDto; onChanged: () => Promise<void> }) {
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const kept = keptVersions(t)
  const previous = kept.find((v) => v.role === 'RollbackTarget')
  const pending = t.versions.filter((v) => v.role === 'Pending')
  const previousDiskMissing = previous !== undefined && !previous.diskPresent
  const headingId = `template-${t.id}`
  const navigate = useNavigate()
  const [jobs, setJobs] = useState<TemplateJobDto[]>([])
  const [jobsLoaded, setJobsLoaded] = useState(false) // Delete waits for it: an active Build must not look absent
  const [dialog, setDialog] = useState<'Edit' | 'Refresh' | null>(null)
  const opener = useRef<HTMLElement | null>(null)
  const activeJob = jobs.find((j) => ACTIVE_JOB_STATES.includes(j.state))
  const canDelete = jobsLoaded && t.versions.length === 0 && !activeJob && !t.isDefault
  const jobBlock = activeJob ? `A ${activeJob.kind} job is already ${activeJob.state.toLowerCase()} for this template.` : undefined

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
        if (!list.some((j) => ACTIVE_JOB_STATES.includes(j.state))) void onChanged()
      }, () => undefined)
    }, JOB_POLL_MS)
    return () => { cancelled = true; clearInterval(timer) }
  }, [polling, t.id, onChanged])

  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    try {
      await action()
      setErrors([])
      await onChanged()
    } catch (e) {
      setErrors(messageOf(e))
    } finally {
      setBusy(false)
    }
  }

  function publish(v: TemplateVersionDto) {
    void run(() => publishTemplateVersion(t.id, v.id))
  }

  function discard(v: TemplateVersionDto) {
    if (window.confirm(`Discard v${v.number} of ${t.displayName}? It is never published and its disk is deleted.`)) void run(() => discardTemplateVersion(t.id, v.id))
  }

  function remove() {
    if (window.confirm(`Delete ${t.displayName}? It has no versions; the template entry is removed.`)) void run(() => deleteTemplate(t.id))
  }

  function rollBack() {
    if (!previous) return
    const ok = window.confirm(
      `Roll back ${t.displayName} to v${previous.number}? New tasks will use v${previous.number}.${t.current ? ` v${t.current.number} will be retired: a rolled-back version is never kept.` : ''}`,
    )
    if (ok) void run(() => rollBackTemplate(t.id))
  }

  return (
    <section className={page.card} aria-labelledby={headingId}>
      <div className={page.head}>
        <h2 id={headingId}>{t.displayName}</h2>
        <code className={page.id}>{t.id}</code>
        {t.isDefault && <span className={page.badge}>Default</span>}
        <span className={page.spacer} />
        <label className={page.toggle}>
          <input type="checkbox" checked={t.enabled} disabled={busy} onChange={(e) => void run(() => setTemplateEnabled(t.id, e.target.checked))} />
          Enabled
        </label>
        <button type="button" className={styles.secondary} disabled={busy || t.isDefault} onClick={() => void run(() => setDefaultTemplate(t.id))}>Set as default</button>
        <button type="button" className={styles.secondary} disabled={busy || !!activeJob} title={jobBlock} onClick={(e) => openDialog('Edit', e.currentTarget)}>Edit</button>
        <button type="button" className={styles.secondary} disabled={busy || !!activeJob} title={jobBlock} onClick={(e) => openDialog('Refresh', e.currentTarget)}>Refresh components</button>
        <button type="button" className={styles.secondary} disabled={busy || !previous || previousDiskMissing} title={previousDiskMissing ? `Cannot roll back: the disk is missing for v${previous.number}.` : undefined} onClick={rollBack}>Roll back</button>
        {canDelete && <button type="button" className={styles.secondary} disabled={busy} onClick={remove}>Delete</button>}
      </div>
      {t.description && <p className={styles.hint}>{t.description}</p>}
      {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
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
                    <button type="button" className={styles.secondary} disabled={busy} onClick={() => publish(v)}>{`Publish v${v.number}`}</button>
                    <button type="button" className={styles.secondary} disabled={busy} onClick={() => discard(v)}>{`Discard v${v.number}`}</button>
                  </span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
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
          <p className={styles.error} role="alert">
            {templates.length > 0 ? `Could not refresh the templates (${loadError}). Showing the last loaded versions. ` : `${loadError} `}
            <button type="button" className={styles.secondary} onClick={() => void refresh()}>Retry</button>
          </p>
        )}
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
