import { useCallback, useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import { ApiError, extendTask, getTask, listArtifacts, stopTask } from '../api/client'
import type { ArtifactDto, RunStatus, TaskDto, TaskStatusUpdate } from '../api/types'
import { EventTable } from '../components/EventTable'
import { ProcessTree } from '../components/ProcessTree'
import { SessionPane } from '../components/SessionPane'
import { StatusBadge, VerdictBadge } from '../components/StatusBadge'
import { useTaskLive } from '../live/useTaskLive'
import { safeText } from '../util/safeText'
import forms from './forms.module.css'
import styles from './TaskPage.module.css'

/** "m:ss" until the deadline, never negative. */
function formatCountdown(deadlineAt: string, now: number): string {
  const ms = Math.max(0, new Date(deadlineAt).getTime() - now)
  const total = Math.ceil(ms / 1000)
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`
}

const COUNTING: readonly RunStatus[] = ['Preparing', 'Running']

function errorMessage(e: unknown): string {
  if (e instanceof ApiError || e instanceof Error) return e.message
  return 'Request failed.'
}

export default function TaskPage() {
  const { id = '' } = useParams()
  return <LiveTask key={id} taskId={id} />
}

function LiveTask({ taskId }: { taskId: string }) {
  const live = useTaskLive(taskId)
  // `basis` is the pushed status that was current when the fetch was issued: only a *different* (newer) push beats it.
  const [fetched, setFetched] = useState<{ task: TaskDto; basis: TaskStatusUpdate | null } | null>(null)
  const task = fetched?.task ?? null
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  // Set by Extend; valid only until the next pushed deadline (`basis`) replaces it.
  const [extended, setExtended] = useState<{ value: string; basis: string | null } | null>(null)
  const [artifacts, setArtifacts] = useState<ArtifactDto[]>([])
  const [now, setNow] = useState(() => Date.now())
  const [busy, setBusy] = useState(false)

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
  }, [taskId, connected])

  // A pushed status carries the authoritative deadline (also after someone else extends the run).
  const liveNow = live.status !== fetched?.basis ? live.status : null // a push older than the last fetch is stale
  const pushedDeadline = liveNow?.deadlineAt ?? null
  const deadlineAt =
    extended && extended.basis === pushedDeadline ? extended.value : (pushedDeadline ?? task?.deadlineAt ?? null)

  const status: RunStatus | null = liveNow?.status ?? task?.status ?? null
  const verdict = liveNow?.verdict ?? task?.verdict ?? 'Unknown'
  const completed = status === 'Completed'

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

  const act = useCallback(async (fn: () => Promise<unknown>) => {
    setBusy(true)
    setActionError(null)
    try {
      await fn()
    } catch (e) {
      setActionError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }, [])

  const extend = (seconds: number) =>
    act(async () => {
      const t = await extendTask(taskId, seconds)
      if (t.deadlineAt) setExtended({ value: t.deadlineAt, basis: pushedDeadline })
    })

  const running = status === 'Running'
  const stoppable = status === 'Preparing' || status === 'Running'
  const target = task?.originalFileName ?? task?.url ?? null

  if (loadError && !task) return <p className={styles.error} role="alert">{loadError}</p>

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
        <button type="button" className={forms.secondary} disabled={!stoppable || busy} onClick={() => act(() => stopTask(taskId))}>Stop</button>
      </div>
      {target && <p className={styles.mono}>{safeText(target)}</p>}
      {task && (
        <p className={styles.meta}>
          <span className={styles.metaLabel}>{task.template ? 'Template' : 'Image'}</span>
          <span className={styles.mono}>{safeText(task.template ? task.template.summary : task.image)}</span>
        </p>
      )}
      {actionError && <p className={styles.error} role="alert">{actionError}</p>}
      {(liveNow?.reason ?? task?.failureReason) && <p className={styles.error}>{safeText(liveNow?.reason ?? task?.failureReason)}</p>}

      {completed && (
        <nav className={styles.links} aria-label="Results">
          <a href={`/api/tasks/${taskId}/report.html`} target="_blank" rel="noreferrer">HTML report</a>
          <a href={`/api/tasks/${taskId}/report.pdf`}>PDF report</a>
          <a href={`/api/tasks/${taskId}/sample`}>Sample (ZIP)</a>
          {artifacts.map((a) => (
            <a key={a.id} href={`/api/artifacts/${a.id}/download`}>{a.kind} ({a.sha256.slice(0, 8)}) (ZIP)</a>
          ))}
        </nav>
      )}

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
        <EventTable events={live.events} />
      </div>
    </section>
  )
}
