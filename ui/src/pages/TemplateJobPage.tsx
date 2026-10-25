import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { cancelTemplateJob, getTemplateJobTunnelToken, provisionLogUrl, sealTemplateJob } from '../api/client'
import type { MeDto, TemplateJobDto, TemplateJobState } from '../api/types'
import LiveConsole from '../components/LiveConsole'
import type { HubConnectionLike } from '../live/useTaskLive'
import { useTemplateJobLive } from '../live/useTemplateJobLive'
import { useConfirm } from '../kit/Confirm'
import { ErrorBanner } from '../kit/ErrorBanner'
import { Skeleton } from '../kit/Skeleton'
import { useToast } from '../kit/Toaster'
import { messageOf } from '../util/apiMessage'
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

function JobView({ job }: { job: TemplateJobDto }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState('')
  const jobRef = useRef(job)
  useEffect(() => { jobRef.current = job })
  // The 202 only records a request: until the server pushes again (it may refuse, e.g. Defender is still on) the button stays off.
  const [acceptedAt, setAcceptedAt] = useState<TemplateJobDto | null>(null)
  const [consoleKey, setConsoleKey] = useState(0) // bumped when a refused seal turns back: the remounted console fetches a fresh token
  const [wasRequested, setWasRequested] = useState(!!job.sealRequested)
  const steps = stepsFor(job)
  const at = progressOf(job, steps)
  const active = ACTIVE.includes(job.state)
  const configuring = job.state === 'Configuring'
  const failed = job.state === 'Failed' || job.state === 'Cancelled'

  const sealRequested = !!job.sealRequested || acceptedAt === job
  if (wasRequested !== !!job.sealRequested) {
    setWasRequested(!!job.sealRequested)
    if (wasRequested && configuring) setConsoleKey((k) => k + 1)
  }

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
        {job.edition && <span className={page.meta}>{job.edition}</span>}
        <Elapsed job={job} />
        <span className={styles.hint}>{`State: ${job.state}`}</span>
        {job.hasProvisionLog && <a href={provisionLogUrl(job.id)}>Provisioning log</a>}
        <Link to="/admin/templates">Back to templates</Link>
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

      {job.message && !failed && <p className={page.message}>{job.message}</p>}
      {job.state === 'Sealed' && <p className={page.message}>Sealed and held: this version is not published. Publish it from the templates page.</p>}
      {failed && <p className={page.failure} role="status">{`${job.state}: ${job.failureReason ?? job.message ?? 'no reason recorded'}`}</p>}

      {configuring && (
        <div className={page.panel}>
          <div className={page.badges}>
            <span className={`${page.badge} ${job.network === 'Direct' ? page.direct : ''}`}>{job.network === 'Direct' ? 'Direct: internet on' : 'None'}</span>
            {job.idleDeadline && <span className={styles.hint}>{`Discarded automatically if idle until ${new Date(job.idleDeadline).toLocaleTimeString()}`}</span>}
          </div>
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
    </section>
  )
}

/** Admin-only. Live over the hub; the console exists only while the job is Configuring. */
export default function TemplateJobPage({ me, createConnection }: { me: MeDto | null; createConnection?: () => HubConnectionLike }) {
  const { id = '' } = useParams()
  const isAdmin = me?.role === 'Admin'
  const { job, loadError } = useTemplateJobLive(isAdmin ? id : null, createConnection)

  if (!me) return <p>Loading…</p>
  if (!isAdmin) return <p role="alert">Administrator access is required to manage sandbox templates.</p>
  if (loadError && !job) return <ErrorBanner message={loadError} onRetry={() => window.location.reload()} />
  if (!job) return <Skeleton rows={4} />
  return <JobView job={job} />
}
