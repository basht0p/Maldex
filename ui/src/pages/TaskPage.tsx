import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ApiError, extendTask, getTask, getTaskReport, listArtifacts, stopTask } from '../api/client'
import type { ArtifactDto, NetworkMode, RunStatus, TaskDto, TaskReportDto, TaskStatusUpdate } from '../api/types'
import { ProcessTree } from '../components/ProcessTree'
import { SessionPane } from '../components/SessionPane'
import { StatusBadge, VerdictBadge } from '../components/StatusBadge'
import { useConfirm } from '../kit/useConfirm'
import { CopyButton } from '../kit/CopyButton'
import { ErrorBanner } from '../kit/ErrorBanner'
import { Duration, RelativeTime } from '../kit/RelativeTime'
import { Skeleton } from '../kit/Skeleton'
import { useToast } from '../kit/useToast'
import { useDownload } from '../kit/useDownload'
import { useTaskLive } from '../live/useTaskLive'
import { safeText } from '../util/safeText'
import forms from './forms.module.css'
import { LiveEventsPanel } from './report/EventsTab'
import { ProcessingWarnings } from './report/shared'
import { TaskReport } from './report/TaskReport'
import styles from './TaskPage.module.css'

/** "m:ss" until the deadline, never negative. */
function formatCountdown(deadlineAt: string, now: number): string {
  const ms = Math.max(0, new Date(deadlineAt).getTime() - now)
  const total = Math.ceil(ms / 1000)
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`
}

const COUNTING: readonly RunStatus[] = ['Preparing', 'Running']
const REPORT_RETRY_MS = 2000

function errorMessage(e: unknown): string {
  if (e instanceof ApiError || e instanceof Error) return e.message
  return 'Request failed.'
}

/** The 409 the report endpoint returns while the server has not yet recorded the task as Completed. */
function isReportNotReady(e: unknown): boolean {
  return e instanceof ApiError && e.status === 409 && typeof e.problem === 'object' && e.problem !== null
    && (e.problem as { type?: unknown }).type === 'report-not-ready'
}

function networkLabel(mode: NetworkMode, exitCountry?: string | null): string {
  switch (mode) {
    case 'None': return 'No network'
    case 'Simulated': return 'Simulated network'
    case 'LiveVpn': return exitCountry ? `Live VPN (${safeText(exitCountry)})` : 'Live VPN'
    case 'LiveTor': return 'Tor'
  }
}

/** organization · submitted by · created · started · completed · ran · template · network mode · MIME type */
function TaskDetails({ task, mime }: { task: TaskDto; mime?: string | null }) {
  const parts: ReactNode[] = [<span key="org">{safeText(task.orgName)}</span>]
  if (task.submittedByName) parts.push(<span key="by">submitted by {safeText(task.submittedByName)}</span>)
  parts.push(<RelativeTime key="created" iso={task.createdAt} />)
  if (task.startedAt) parts.push(<span key="started">started <RelativeTime iso={task.startedAt} /></span>)
  if (task.completedAt) parts.push(<span key="completed">completed <RelativeTime iso={task.completedAt} /></span>)
  if (task.ranSec != null) parts.push(<span key="ran">ran <Duration seconds={task.ranSec} /></span>)
  parts.push(
    <span key="tpl">
      <span className={styles.metaLabel}>{task.template ? 'Template' : 'Image'}</span>{' '}
      <span className={styles.mono}>{safeText(task.template ? task.template.summary : task.image)}</span>
    </span>,
  )
  parts.push(<span key="net">{networkLabel(task.networkMode, task.exitCountry)}</span>)
  if (mime) parts.push(<span key="mime" className={styles.mono}>{safeText(mime)}</span>)
  return (
    <div className={styles.meta} role="group" aria-label="Task details">
      {parts.flatMap((p, i) => (i === 0 ? [p] : [<span key={`sep${i}`} aria-hidden="true" className={styles.sep}>·</span>, p]))}
    </div>
  )
}

/** HTML/PDF report, the sample and the artifact ZIPs, behind one disclosure button. */
function ReportsMenu({ taskId, sampleSha256, artifacts }: { taskId: string; sampleSha256?: string | null; artifacts: ArtifactDto[] }) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  const download = useDownload()
  const id = encodeURIComponent(taskId)
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])
  return (
    <div className={styles.menu} ref={ref}>
      <button type="button" className={forms.secondary} aria-expanded={open} aria-controls={`reports-${taskId}`} onClick={() => setOpen((o) => !o)}>
        Reports
      </button>
      {open && (
        <ul id={`reports-${taskId}`} className={styles.menuList}>
          {/* A plain link: it opens in a new tab and relies on the response's own CSP, which a blob: URL would drop.
              The session cookie authenticates it. */}
          <li><a href={`/api/tasks/${id}/report.html`} target="_blank" rel="noreferrer">HTML report</a></li>
          <li><button type="button" className={styles.menuButton} onClick={() => void download(`/api/tasks/${id}/report.pdf`, `maldex-${taskId}.pdf`)}>PDF report</button></li>
          <li><button type="button" className={styles.menuButton} onClick={() => void download(`/api/tasks/${id}/sample`, `${sampleSha256 ?? taskId}.zip`)}>Sample (ZIP)</button></li>
          {artifacts.map((a) => (
            <li key={a.id}>
              <button type="button" className={styles.menuButton} onClick={() => void download(`/api/artifacts/${encodeURIComponent(a.id)}/download`, `${a.sha256}.zip`)}>
                {a.kind} ({a.sha256.slice(0, 8)}) (ZIP)
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

export default function TaskPage() {
  const { id = '' } = useParams()
  return <LiveTask key={id} taskId={id} />
}

function LiveTask({ taskId }: { taskId: string }) {
  const live = useTaskLive(taskId)
  const toast = useToast()
  const confirm = useConfirm()
  const [reload, setReload] = useState(0)
  // `basis` is the pushed status that was current when the fetch was issued: only a *different* (newer) push beats it.
  const [fetched, setFetched] = useState<{ task: TaskDto; basis: TaskStatusUpdate | null } | null>(null)
  const task = fetched?.task ?? null
  const [loadError, setLoadError] = useState<string | null>(null)
  // Set by Extend; valid only until the next pushed deadline (`basis`) replaces it.
  const [extended, setExtended] = useState<{ value: string; basis: string | null } | null>(null)
  const [artifacts, setArtifacts] = useState<ArtifactDto[]>([])
  const [now, setNow] = useState(() => Date.now())
  const [busy, setBusy] = useState(false)
  // Set once when a task first seen Queued is seen leaving it: its template version is bound when the run starts.
  const [leftQueue, setLeftQueue] = useState(false)

  // Fetched on mount and again whenever the hub (re)connects: a status push missed during an outage is not in the
  // hub backlog, so this is what reconciles status, verdict and the report links after a reconnect.
  const connected = live.connected
  const pushedRef = useRef<TaskStatusUpdate | null>(null)
  useEffect(() => {
    pushedRef.current = live.status // declared before the fetch effect, so it is current when that one runs
  })
  useEffect(() => {
    let cancelled = false
    const basis = pushedRef.current
    getTask(taskId).then(
      (t) => {
        if (cancelled) return
        setFetched({ task: t, basis })
        setLoadError(null)
      },
      (e: unknown) => {
        if (!cancelled) setLoadError(errorMessage(e))
      },
    )
    return () => {
      cancelled = true
    }
  }, [taskId, connected, leftQueue, reload])

  // A pushed status carries the authoritative deadline (also after someone else extends the run).
  const liveNow = live.status !== fetched?.basis ? live.status : null // a push older than the last fetch is stale
  const pushedDeadline = liveNow?.deadlineAt ?? null
  const deadlineAt =
    extended && extended.basis === pushedDeadline ? extended.value : (pushedDeadline ?? task?.deadlineAt ?? null)

  const status: RunStatus | null = liveNow?.status ?? task?.status ?? null
  // Fetch once more after the task leaves Queued, so the Template line appears without a reload.
  if (!leftQueue && task?.status === 'Queued' && status !== null && status !== 'Queued') setLeftQueue(true)
  const verdict = liveNow?.verdict ?? task?.verdict ?? 'Unknown'
  const completed = status === 'Completed'

  // The report is fetched once the task is Completed (from a push or the fetch). A 409 report-not-ready means the
  // server has not caught up with the push yet: it is not an error. The next status push retries, and so does a
  // single timer 2 s later (a completed task may get no further push).
  const [report, setReport] = useState<TaskReportDto | null>(null)
  const [reportError, setReportError] = useState<string | null>(null)
  const [reportReload, setReportReload] = useState(0)
  const notReadyRetried = useRef(false)
  const push = live.status
  useEffect(() => {
    if (!completed || report) return
    let cancelled = false
    let retry: ReturnType<typeof setTimeout> | undefined
    getTaskReport(taskId).then(
      (r) => {
        if (cancelled) return
        setReport(r)
        setReportError(null)
      },
      (e: unknown) => {
        if (cancelled) return
        if (!isReportNotReady(e)) setReportError(errorMessage(e))
        else if (!notReadyRetried.current) {
          retry = setTimeout(() => {
            notReadyRetried.current = true
            setReportReload((n) => n + 1)
          }, REPORT_RETRY_MS)
        }
      },
    )
    return () => {
      cancelled = true
      clearTimeout(retry)
    }
  }, [taskId, completed, report, push, reportReload])

  useEffect(() => {
    if (!completed) return
    let cancelled = false
    listArtifacts(taskId).then(
      (list) => {
        if (!cancelled) setArtifacts(list)
      },
      () => undefined,
    )
    return () => {
      cancelled = true
    }
  }, [taskId, completed])

  const counting = status !== null && COUNTING.includes(status) && deadlineAt !== null
  useEffect(() => {
    if (!counting) return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [counting])

  const act = useCallback(async (fn: () => Promise<unknown>, done: string) => {
    setBusy(true)
    try {
      await fn()
      toast.success(done)
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }, [toast])

  const extend = (seconds: number) =>
    act(async () => {
      const t = await extendTask(taskId, seconds)
      if (t.deadlineAt) setExtended({ value: t.deadlineAt, basis: pushedDeadline })
    }, `Extended by ${seconds} s`)

  async function stop() {
    if (!(await confirm({ title: 'Stop this task?', body: 'The run ends now.', confirmLabel: 'Stop task', danger: true }))) return
    await act(() => stopTask(taskId), 'Task stopped')
  }

  const running = status === 'Running'
  const stoppable = status === 'Preparing' || status === 'Running'
  const target = task?.originalFileName ?? task?.url ?? null
  const details = report?.task ?? task

  if (loadError && !task) return <ErrorBanner message={loadError} onRetry={() => { setLoadError(null); setReload((n) => n + 1) }} />
  if (!task && !status) return <Skeleton rows={4} />

  return (
    <section>
      <div className={styles.header}>
        <h1>Task</h1>
        {status && <StatusBadge status={status} />}
        {status && status !== 'Queued' && <VerdictBadge verdict={verdict} />}
        {counting && deadlineAt && <span className={styles.countdown} aria-label="Time remaining">{formatCountdown(deadlineAt, now)}</span>}
        {!live.connected && <span className={styles.offline}>Connecting to live updates...</span>}
        <span className={styles.spacer} />
        <button type="button" className={forms.secondary} disabled={!running || busy} onClick={() => extend(60)}>Extend +60 s</button>
        <button type="button" className={forms.secondary} disabled={!running || busy} onClick={() => extend(300)}>Extend +300 s</button>
        <button type="button" className={forms.secondary} disabled={!stoppable || busy} onClick={() => void stop()}>Stop</button>
        {completed && <ReportsMenu taskId={taskId} sampleSha256={task?.sampleSha256} artifacts={artifacts} />}
        {task && <Link className={`${forms.secondary} ${styles.linkButton}`} to={`/submit?from=${encodeURIComponent(taskId)}`}>Resubmit…</Link>}
      </div>
      {target && (
        <p className={styles.mono}>
          {safeText(target)}
          {task?.url && !task.originalFileName && <CopyButton value={task.url} label="Copy URL" />}
        </p>
      )}
      {task?.sampleSha256 && (
        <p className={styles.meta}>
          <span className={styles.metaLabel}>SHA-256</span>
          <span className={styles.mono}>{task.sampleSha256}</span>
          <CopyButton value={task.sampleSha256} label="Copy SHA-256" />
        </p>
      )}
      {details && <TaskDetails task={details} mime={report?.mime} />}
      {(liveNow?.reason ?? task?.failureReason) && <p className={styles.error}>{safeText(liveNow?.reason ?? task?.failureReason)}</p>}

      {!completed && <ProcessingWarnings warnings={task?.processingWarnings} />}
      {completed ? (
        report ? (
          <TaskReport report={report} taskId={taskId} eventCount={live.events.length} />
        ) : reportError ? (
          <ErrorBanner message={reportError} onRetry={() => { setReportError(null); setReportReload((n) => n + 1) }} />
        ) : (
          <Skeleton rows={6} />
        )
      ) : (
        <>
          <div className={styles.grid}>
            <div className={styles.panel}>
              <h2>Session</h2>
              <SessionPane taskId={taskId} status={status} />
            </div>
            <div className={styles.panel}>
              <h2>Processes</h2>
              <ProcessTree processes={live.processes} />
            </div>
          </div>
          <div className={styles.panel}>
            <h2>Events</h2>
            <LiveEventsPanel events={live.events} processes={live.processes} />
          </div>
        </>
      )}
    </section>
  )
}
