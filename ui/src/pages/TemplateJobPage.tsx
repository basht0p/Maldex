import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { cancelTemplateJob, downloadFile, fetchBlob, getTemplateJobTunnelToken, provisionLogUrl, sealTemplateJob } from '../api/client'
import type { MeDto, TemplateJobDto, TemplateJobState } from '../api/types'
import LiveConsole from '../components/LiveConsole'
import type { HubConnectionLike } from '../live/useTaskLive'
import { useTemplateJobLive } from '../live/useTemplateJobLive'
import { useConfirm } from '../kit/useConfirm'
import { ErrorBanner } from '../kit/ErrorBanner'
import { CopyButton } from '../kit/CopyButton'
import { formatDuration } from '../kit/format'
import { Duration, RelativeTime } from '../kit/RelativeTime'
import { Skeleton } from '../kit/Skeleton'
import { useToast } from '../kit/useToast'
import { messageOf } from '../util/apiMessage'
import { safeText } from '../util/safeText'
import { safeLines } from './report/reportUtil'
import styles from './forms.module.css'
import page from './TemplateJobPage.module.css'

const ACTIVE: TemplateJobState[] = ['Queued', 'Preparing', 'Installing', 'Provisioning', 'Configuring', 'Sealing', 'Verifying']
const EDIT_STEPS: TemplateJobState[] = ['Queued', 'Preparing', 'Configuring', 'Sealing', 'Verifying', 'Published']
const BUILD_STEPS: TemplateJobState[] = ['Queued', 'Preparing', 'Installing', 'Provisioning', 'Configuring', 'Sealing', 'Verifying', 'Published']

/** The steps of a job in order: a Build installs and provisions first; a Refresh has no Configuring step (nobody works in the guest). */
function stepsFor(job: TemplateJobDto): TemplateJobState[] {
  if (job.kind === 'Build') return BUILD_STEPS
  return job.kind === 'Refresh' ? EDIT_STEPS.filter((s) => s !== 'Configuring') : EDIT_STEPS
}

function headingOf(job: TemplateJobDto): string {
  return job.kind === 'Build' ? 'New template (build)' : job.kind === 'Edit' ? 'Edit' : 'Refresh components'
}

/** Index of the step the job is at; `Sealed` (held, never published) has passed Verifying and shows no current step. */
function progressOf(job: TemplateJobDto, steps: TemplateJobState[]): number {
  if (job.state === 'Sealed') return steps.indexOf('Published') // all but the last step are done
  return steps.indexOf(job.state) // -1 for Failed/Cancelled
}

function formatElapsed(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  const two = (n: number) => String(n).padStart(2, '0')
  return h > 0 ? `${h}:${two(m)}:${two(s)}` : `${two(m)}:${two(s)}`
}

function Elapsed({ job }: { job: TemplateJobDto }) {
  const ended = job.endedAt ? Date.parse(job.endedAt) : null
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (ended !== null) return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [ended])
  return <span className={page.meta}>{`Elapsed ${formatElapsed((ended ?? now) - Date.parse(job.createdAt))}`}</span>
}

/**
 * Messages of a refused Seal. Keep in step with TemplateCoordinator.DefenderStillOn and .DefenderUnconfirmed (C#);
 * the normal Configuring guidance (DefenderGuidance) is not a refusal and shows as the plain message.
 * Pinned by TemplateRefusalMessageTests (tests/Maldex.Integration.Tests/Orchestrator).
 */
const REFUSAL_PREFIXES = [
  'Defender real-time protection is still on',
  'Defender state could not be confirmed',
]

const TERMINAL: TemplateJobState[] = ['Published', 'Sealed', 'Failed', 'Cancelled']
const MAX_LOG_BYTES = 1024 * 1024

/** A clock that ticks every second while `on`. */
function useNow(on: boolean): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!on) return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [on])
  return now
}

/** Queued at creation, then one row per recorded step with the time spent in it; the current step's time ticks. */
function Timeline({ job }: { job: TemplateJobDto }) {
  const steps = job.steps ?? []
  const terminal = TERMINAL.includes(job.state)
  const now = useNow(!terminal && steps.length > 0)
  const [seen, setSeen] = useState(() => ({ state: job.state, at: new Date().toISOString() }))
  if (seen.state !== job.state) setSeen({ state: job.state, at: new Date(now).toISOString() }) // when this push arrived
  if (steps.length === 0) {
    return <p className={page.since}>{job.state} since <RelativeTime iso={job.createdAt} /></p>
  }
  const rows: { state: TemplateJobState; at: string }[] = [{ state: 'Queued', at: job.createdAt }, ...steps]
  if (rows[rows.length - 1].state !== job.state) rows.push({ state: job.state, at: seen.at }) // a push that lacked the newest step
  const ms = (iso: string) => Date.parse(iso)
  return (
    <ol className={page.timeline} aria-label="Timeline">
      {rows.map((r, i) => {
        const last = i === rows.length - 1
        const end = last ? (terminal ? null : now) : ms(rows[i + 1].at)
        const seconds = end === null ? null : Math.max(0, (end - ms(r.at)) / 1000)
        return (
          <li key={`${i}-${r.state}`}>
            <span className={page.tlState}>{r.state}</span>
            <RelativeTime iso={r.at} />
            {seconds !== null && <span className={page.tlDuration}><Duration seconds={seconds} /></span>}
          </li>
        )
      })}
    </ol>
  )
}

function IdleCountdown({ deadline }: { deadline: string }) {
  const now = useNow(true)
  const left = Math.max(0, Math.ceil((Date.parse(deadline) - now) / 1000))
  return <span className={styles.hint} role="timer">{`Discarded in ${formatDuration(left)} if the console stays closed`}</span>
}

const DEFENDER_STEPS = [
  'Open Windows Security',
  'Virus and threat protection → Manage settings',
  'Turn off Tamper Protection',
  'Turn off Real-time protection',
  'Press Seal',
]

function DefenderChecklist({ refusal }: { refusal: string | null }) {
  return (
    <div className={page.checklist}>
      <div>
        <h2 className={page.checkTitle}>Turn Defender off before sealing</h2>
        <ol>{DEFENDER_STEPS.map((t) => <li key={t}>{t}</li>)}</ol>
      </div>
      {refusal && <p className={page.refusal} role="status">{safeText(refusal)}</p>}
    </div>
  )
}

/** The Build's provisioning log inline as text (never markup), at most 1 MiB; the rest is a download. */
function ProvisionLog({ jobId }: { jobId: string }) {
  const toast = useToast()
  const [log, setLog] = useState<{ text: string; truncated: boolean } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    let cancelled = false
    fetchBlob(provisionLogUrl(jobId))
      .then(async (blob) => {
        const truncated = blob.size > MAX_LOG_BYTES
        const text = await (truncated ? blob.slice(0, MAX_LOG_BYTES) : blob).text()
        if (!cancelled) setLog({ text, truncated })
      })
      .catch((e) => { if (!cancelled) setError(messageOf(e).join(' ') || 'Could not load the provisioning log.') })
    return () => { cancelled = true }
  }, [jobId, attempt])

  return (
    <section className={page.log} aria-labelledby="prov-log-heading">
      <div className={page.logHead}>
        <h2 id="prov-log-heading" className={page.checkTitle}>Provisioning log</h2>
        {log && <CopyButton value={log.text} label="Copy provisioning log" />}
      </div>
      {error && <ErrorBanner message={error} onRetry={() => { setError(null); setAttempt((n) => n + 1) }} />}
      {!error && !log && <Skeleton rows={3} />}
      {log && <pre className={page.logText}>{safeLines(log.text)}</pre>}
      {log?.truncated && (
        <button type="button" className={styles.secondary}
          onClick={() => void downloadFile(provisionLogUrl(jobId), `provision-${jobId}.log`).catch((e) => { for (const m of messageOf(e)) toast.error(m) })}>
          Download the full log
        </button>
      )}
    </section>
  )
}

function JobView({ job }: { job: TemplateJobDto }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState('')
  const jobRef = useRef(job)
  useEffect(() => { jobRef.current = job })
  // The 202 only records a request: until the server pushes again (it may refuse, e.g. Defender is still on) the button stays off.
  const [acceptedAt, setAcceptedAt] = useState<TemplateJobDto | null>(null)
  const [expanded, setExpanded] = useState(false)
  const fullscreenEntered = useRef(false)
  const [consoleKey, setConsoleKey] = useState(0) // bumped when a refused seal turns back: the remounted console fetches a fresh token
  const [wasRequested, setWasRequested] = useState(!!job.sealRequested)
  const steps = stepsFor(job)
  const at = progressOf(job, steps)
  const active = ACTIVE.includes(job.state)
  const configuring = job.state === 'Configuring'
  const refusal = configuring && job.message && REFUSAL_PREFIXES.some((p) => job.message!.startsWith(p)) ? job.message : null
  const failed = job.state === 'Failed' || job.state === 'Cancelled'

  const sealRequested = !!job.sealRequested || acceptedAt === job
  if (wasRequested !== !!job.sealRequested) {
    setWasRequested(!!job.sealRequested)
    if (wasRequested && configuring) setConsoleKey((k) => k + 1)
  }

  function toggleExpanded() {
    const next = !expanded
    setExpanded(next)
    if (next) void document.documentElement.requestFullscreen?.()?.catch?.(() => undefined)
    else if (document.fullscreenElement) void document.exitFullscreen?.()?.catch?.(() => undefined)
  }
  if (!configuring && expanded) setExpanded(false) // the console is gone: a later one must not start expanded
  useEffect(() => {
    if (!configuring && document.fullscreenElement) void document.exitFullscreen?.()?.catch?.(() => undefined)
  }, [configuring])
  useEffect(() => { // leaving fullscreen with Esc restores the layout too
    if (!expanded) { fullscreenEntered.current = false; return }
    const onChange = () => {
      if (document.fullscreenElement) fullscreenEntered.current = true
      else if (fullscreenEntered.current) setExpanded(false)
    }
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [expanded])

  async function run(action: () => Promise<unknown>, done: string): Promise<boolean> {
    setBusy(true)
    try {
      await action()
      toast.success(done)
      return true
    } catch (e) {
      for (const m of messageOf(e)) toast.error(m)
      return false
    } finally {
      setBusy(false)
    }
  }

  async function seal() {
    if (await run(() => sealTemplateJob(job.id, note.trim()), 'Seal request sent')) setAcceptedAt(jobRef.current)
  }

  async function discard() {
    const ok = await confirm({
      title: 'Discard this job?',
      body: 'The working copy is deleted and no version is created.',
      confirmLabel: 'Discard',
      danger: true,
    })
    if (ok) await run(() => cancelTemplateJob(job.id), 'Job discarded')
  }

  return (
    <section aria-labelledby="job-heading">
      <div className={page.head}>
        <h1 id="job-heading">{headingOf(job)}: <span className={styles.mono}>{job.templateId}</span></h1>
        {(job.baseVersionNumber != null || job.resultVersionNumber != null) && (
          <span className={page.meta}>{[job.baseVersionNumber != null ? `from v${job.baseVersionNumber}` : '', job.resultVersionNumber != null ? `to v${job.resultVersionNumber}` : ''].filter(Boolean).join(' ')}</span>
        )}
        {job.edition && <span className={page.meta}>{safeText(job.edition)}</span>}
        <Elapsed job={job} />
        <span className={styles.hint}>{`State: ${job.state}`}</span>
        {job.requestedByName && <span className={page.meta}>{`Requested by ${safeText(job.requestedByName)}`}</span>}
        <Link to={`/admin/templates/${job.templateId}`}>Back to the template</Link>
        <span style={{ flex: 1 }} />
        {active && !configuring && (
          <button type="button" className={styles.secondary} disabled={busy} onClick={() => void run(() => cancelTemplateJob(job.id), 'Job cancelled')}>Cancel</button>
        )}
      </div>

      <ol className={page.tracker} aria-label="Progress">
        {steps.map((s, i) => {
          const current = i === at && job.state !== 'Sealed'
          const done = i < at
          return (
            <li key={s} className={`${page.step} ${current ? page.current : ''} ${done ? page.done : ''}`} aria-current={current ? 'step' : undefined}>
              {s}
            </li>
          )
        })}
      </ol>

      <Timeline job={job} />

      {job.message && !failed && !refusal && <p className={page.message}>{safeText(job.message)}</p>}
      {job.state === 'Sealed' && <p className={page.message}>Sealed and held: this version is not published. Publish it from <Link to={`/admin/templates/${job.templateId}`}>the template&apos;s page</Link>.</p>}
      {failed && <p className={page.failure} role="status">{`${job.state}: ${safeText(job.failureReason ?? job.message) || 'no reason recorded'}`}</p>}

      {configuring && (
        <div className={`${page.panel} ${expanded ? page.expanded : ''}`}>
          <div className={page.badges}>
            <span className={`${page.badge} ${job.network === 'Direct' ? page.direct : ''}`}>{job.network === 'Direct' ? 'Direct: internet on' : 'None'}</span>
            {job.idleDeadline && <IdleCountdown deadline={job.idleDeadline} />}
            <button type="button" className={styles.secondary} onClick={toggleExpanded}>{expanded ? 'Exit' : 'Expand'}</button>
          </div>
          <DefenderChecklist refusal={refusal} />
          <LiveConsole key={consoleKey} getToken={() => getTemplateJobTunnelToken(job.id)} tunnelPath={`/api/admin/template-jobs/${encodeURIComponent(job.id)}/tunnel`} />
          <div className={page.actions}>
            <div className={`${styles.field} ${page.note}`}>
              <label htmlFor="seal-note">Seal note (optional)</label>
              <input id="seal-note" type="text" value={note} disabled={sealRequested} onChange={(e) => setNote(e.target.value)} />
            </div>
            <button type="button" className={styles.primary} disabled={busy || sealRequested} onClick={() => void seal()}>Seal</button>
            {sealRequested && <span className={styles.hint} role="status">Seal requested</span>}
            <button type="button" className={styles.secondary} disabled={busy} onClick={() => void discard()}>Discard</button>
          </div>
        </div>
      )}
      {job.hasProvisionLog && <ProvisionLog jobId={job.id} />}
    </section>
  )
}

/** Admin-only. Live over the hub; the console exists only while the job is Configuring. */
export default function TemplateJobPage({ me, createConnection }: { me: MeDto | null; createConnection?: () => HubConnectionLike }) {
  const { id = '' } = useParams()
  const isAdmin = me?.role === 'Admin'
  const { job, loadError, retry } = useTemplateJobLive(isAdmin ? id : null, createConnection)

  if (!me) return <p>Loading…</p>
  if (!isAdmin) return <p role="alert">Administrator access is required to manage sandbox templates.</p>
  if (loadError && !job) return <ErrorBanner message={loadError} onRetry={retry} />
  if (!job) return <Skeleton rows={4} />
  return <JobView job={job} />
}
