import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { cancelTemplateJob, getTemplateJobTunnelToken, sealTemplateJob } from '../api/client'
import type { MeDto, TemplateJobDto, TemplateJobState } from '../api/types'
import LiveConsole from '../components/LiveConsole'
import type { HubConnectionLike } from '../live/useTaskLive'
import { useTemplateJobLive } from '../live/useTemplateJobLive'
import { messageOf } from '../util/apiMessage'
import styles from './forms.module.css'
import page from './TemplateJobPage.module.css'

const ACTIVE: TemplateJobState[] = ['Queued', 'Preparing', 'Configuring', 'Sealing', 'Verifying']
/** The steps of a job in order; a Refresh has no Configuring step (nobody works in the guest). */
const STEPS: TemplateJobState[] = ['Queued', 'Preparing', 'Configuring', 'Sealing', 'Verifying', 'Published']

function stepsFor(job: TemplateJobDto): TemplateJobState[] {
  return job.kind === 'Refresh' ? STEPS.filter((s) => s !== 'Configuring') : STEPS
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
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState('')
  const [sealedIn, setSealedIn] = useState<TemplateJobState | null>(null) // the state a Seal was accepted in
  const steps = stepsFor(job)
  const at = progressOf(job, steps)
  const active = ACTIVE.includes(job.state)
  const configuring = job.state === 'Configuring'
  const failed = job.state === 'Failed' || job.state === 'Cancelled'

  const sealRequested = sealedIn === job.state // the 202 is only a request: wait for the state to change

  async function run(action: () => Promise<unknown>): Promise<boolean> {
    setBusy(true)
    try {
      await action()
      setErrors([])
      return true
    } catch (e) {
      setErrors(messageOf(e))
      return false
    } finally {
      setBusy(false)
    }
  }

  async function seal() {
    const state = job.state
    if (await run(() => sealTemplateJob(job.id, note.trim()))) setSealedIn(state)
  }

  function discard() {
    if (window.confirm('Discard this job? The working copy is deleted and no version is created.')) void run(() => cancelTemplateJob(job.id))
  }

  return (
    <section aria-labelledby="job-heading">
      <div className={page.head}>
        <h1 id="job-heading">{job.kind === 'Edit' ? 'Edit' : 'Refresh components'}: <span className={styles.mono}>{job.templateId}</span></h1>
        <span className={page.meta}>{`from v${job.baseVersionNumber}${job.resultVersionNumber != null ? ` to v${job.resultVersionNumber}` : ''}`}</span>
        <Elapsed job={job} />
        <span className={styles.hint}>{`State: ${job.state}`}</span>
        <Link to="/admin/templates">Back to templates</Link>
        <span style={{ flex: 1 }} />
        {active && !configuring && (
          <button type="button" className={styles.secondary} disabled={busy} onClick={() => void run(() => cancelTemplateJob(job.id))}>Cancel</button>
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

      {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
      {job.message && !failed && <p className={page.message}>{job.message}</p>}
      {job.state === 'Sealed' && <p className={page.message}>Sealed and held: this version is not published. Publish it from the templates page.</p>}
      {failed && <p className={page.failure} role="status">{`${job.state}: ${job.failureReason ?? job.message ?? 'no reason recorded'}`}</p>}

      {configuring && (
        <div className={page.panel}>
          <div className={page.badges}>
            <span className={`${page.badge} ${job.network === 'Direct' ? page.direct : ''}`}>{job.network === 'Direct' ? 'Direct: internet on' : 'None'}</span>
            {job.idleDeadline && <span className={styles.hint}>{`Discarded automatically if idle until ${new Date(job.idleDeadline).toLocaleTimeString()}`}</span>}
          </div>
          <LiveConsole getToken={() => getTemplateJobTunnelToken(job.id)} tunnelPath={`/api/admin/template-jobs/${encodeURIComponent(job.id)}/tunnel`} />
          <div className={page.actions}>
            <div className={`${styles.field} ${page.note}`}>
              <label htmlFor="seal-note">Seal note (optional)</label>
              <input id="seal-note" type="text" value={note} disabled={sealRequested} onChange={(e) => setNote(e.target.value)} />
            </div>
            <button type="button" className={styles.primary} disabled={busy || sealRequested} onClick={() => void seal()}>Seal</button>
            {sealRequested && <span className={styles.hint} role="status">Seal requested</span>}
            <button type="button" className={styles.secondary} disabled={busy} onClick={discard}>Discard</button>
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
  if (loadError && !job) return <p className={styles.error} role="alert">{loadError}</p>
  if (!job) return <p>Loading…</p>
  return <JobView job={job} />
}
