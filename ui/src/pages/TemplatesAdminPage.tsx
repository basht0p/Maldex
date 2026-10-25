import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { listAdminTemplates } from '../api/client'
import type { AdminTemplateDto, MeDto, TemplateVersionDto } from '../api/types'
import { ErrorBanner } from '../kit/ErrorBanner'
import { RelativeTime } from '../kit/RelativeTime'
import { Skeleton } from '../kit/Skeleton'
import { messageOf } from '../util/apiMessage'
import { safeText } from '../util/safeText'
import styles from './forms.module.css'
import NewTemplateDialog from './NewTemplateDialog'
import JobDialog from './templates/JobDialog'
import { useTemplateActions } from './templates/useTemplateActions'
import { ALREADY_DEFAULT, JOB_ACTIVE, JOB_POLL_MS, NO_VERSION, windowsLabel } from './templates/versions'
import page from './TemplatesAdminPage.module.css'

/** e.g. `v3 · Windows 11 Pro 26100.x · Office: no · agent 1.4.2`; unknown parts are left out. */
function versionLine(v: TemplateVersionDto): string {
  const parts = [`v${v.number}`]
  const windows = windowsLabel(v)
  if (windows) parts.push(safeText(windows))
  if (v.officePresent != null) parts.push(`Office: ${v.officePresent ? 'yes' : 'no'}`)
  if (v.agentVersion) parts.push(`agent ${safeText(v.agentVersion)}`)
  return parts.join(' · ')
}

function TemplateCard({ template: t, onChanged }: { template: AdminTemplateDto; onChanged: () => Promise<void> }) {
  const { busy, makeDefault } = useTemplateActions(t, onChanged)
  const navigate = useNavigate()
  const [dialog, setDialog] = useState<'Edit' | 'Refresh' | null>(null)
  const opener = useRef<HTMLElement | null>(null)
  const headingId = `template-${t.id}`
  const noVersionId = `${headingId}-no-version`
  const jobReason = t.current ? (t.activeJobId ? JOB_ACTIVE : undefined) : NO_VERSION

  function openDialog(kind: 'Edit' | 'Refresh', trigger: HTMLElement) {
    opener.current = trigger
    setDialog(kind)
  }

  function closeDialog() {
    setDialog(null)
    opener.current?.focus() // focus returns to the trigger
  }

  const jobButton = (kind: 'Edit' | 'Refresh') => (
    <button type="button" className={styles.secondary} disabled={busy || !!jobReason} title={jobReason} aria-describedby={!t.current ? noVersionId : undefined} onClick={(e) => openDialog(kind, e.currentTarget)}>{kind}</button>
  )

  return (
    <section className={page.card} aria-labelledby={headingId}>
      <div className={page.head}>
        <h2 id={headingId}><Link to={`/admin/templates/${t.id}`}>{safeText(t.displayName)}</Link></h2>
        <code className={page.id}>{t.id}</code>
        {t.isDefault && <span className={page.badge}>Default</span>}
        {!t.enabled && <span className={page.badge}>Disabled</span>}
        {t.activeJobId && <Link className={page.badge} to={`/admin/template-jobs/${t.activeJobId}`}>Active job</Link>}
        {t.refreshDue && <span className={`${page.badge} ${page.badgeWarn}`}>Refresh due</span>}
      </div>
      {t.description && <p className={styles.hint}>{safeText(t.description)}</p>}
      <p className={page.line}>
        {t.current ? <>
          <span className={styles.mono}>{versionLine(t.current)}</span>{' '}
          <span className={styles.hint}>sealed <RelativeTime iso={t.current.sealedAt} /></span>
        </> : <span className={styles.hint}>{t.versions.length === 0 ? 'No version yet' : 'No current version'}</span>}
      </p>
      <p className={styles.hint}>{t.tasksLast30Days === 1 ? '1 task in the last 30 days' : `${t.tasksLast30Days} tasks in the last 30 days`}</p>
      <div className={page.actions}>
        {jobButton('Edit')}
        {jobButton('Refresh')}
        {!t.current && <span id={noVersionId} hidden>{NO_VERSION}</span>}
        <button type="button" className={styles.secondary} disabled={busy || t.isDefault} title={t.isDefault ? ALREADY_DEFAULT : undefined} onClick={() => void makeDefault()}>Set default</button>
        <Link className={`${styles.secondary} ${page.linkButton}`} to={`/admin/templates/${t.id}`}>Details</Link>
      </div>
      {dialog && <JobDialog kind={dialog} template={t} onClose={closeDialog} onStarted={(job) => navigate(`/admin/template-jobs/${job.id}`)} />}
    </section>
  )
}

/** Admin-only. Non-admins see a notice; the API enforces the same rule server-side. */
export default function TemplatesAdminPage({ me }: { me: MeDto | null }) {
  const [templates, setTemplates] = useState<AdminTemplateDto[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)
  const [building, setBuilding] = useState(false)
  const opener = useRef<HTMLElement | null>(null)
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const isAdmin = me?.role === 'Admin'
  const legacyMedia = params.get('tab') === 'media'

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
    if (legacyMedia) navigate('/admin/media', { replace: true }) // the Media tab moved to its own page
  }, [legacyMedia, navigate])

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- initial fetch
    if (isAdmin && !legacyMedia) void refresh()
  }, [isAdmin, legacyMedia, refresh])

  // While any template has a job running, reload so a finished job shows up and Edit/Refresh come back.
  const polling = templates.some((t) => !!t.activeJobId)
  useEffect(() => {
    if (!polling) return
    const timer = setInterval(() => void refresh(), JOB_POLL_MS)
    return () => clearInterval(timer)
  }, [polling, refresh])

  if (!me) return <p>Loading…</p>
  if (!isAdmin) return <p role="alert">Administrator access is required to manage sandbox templates.</p>
  if (legacyMedia) return null

  function closeBuild() {
    setBuilding(false)
    opener.current?.focus() // focus returns to the trigger
  }

  return (
    <section>
      <h1>Sandbox Templates</h1>
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
    </section>
  )
}
