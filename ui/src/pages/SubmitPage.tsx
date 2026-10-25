import { type FormEvent, useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError, getTask, listOrgs, listTasks, listTemplates, resubmitTask, submitTask } from '../api/client'
import type { NetworkMode, OrgDto, ProblemDetails, TaskDto, TemplateSummaryDto } from '../api/types'
import { VerdictBadge } from '../components/StatusBadge'
import { RelativeTime } from '../kit/RelativeTime'
import { useToast } from '../kit/useToast'
import { messageOf } from '../util/apiMessage'
import { safeText } from '../util/safeText'
import { DropZone, type DropItem } from './submit/DropZone'
import dropStyles from './submit/DropZone.module.css'
import { loadLastOptions, saveLastOptions } from './submit/lastOptions'
import { sha256Hex } from './submit/sha256'
import styles from './forms.module.css'
import page from './SubmitPage.module.css'

type FieldErrors = Record<string, string[]>

const MAX_URL_LENGTH = 2048
const SUBMITTABLE_MODES: NetworkMode[] = ['Simulated', 'None', 'LiveVpn']

/** Client-side mirror of the server's URL rule (absolute http/https); the server stays authoritative. */
function urlError(raw: string): string | null {
  if (raw.length > MAX_URL_LENGTH) return `The URL must be at most ${MAX_URL_LENGTH} characters.`
  let parsed: URL
  try {
    parsed = new URL(raw)
  } catch {
    return 'The URL must be an absolute http or https URL.'
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return 'The URL must be an absolute http or https URL.'
  return null
}

type RowState = 'idle' | 'queued' | 'uploading' | 'created' | 'failed'
type Entry = {
  id: number
  file: File
  state: RowState
  taskId?: string
  error?: string
  dup?: { total: number; latest: TaskDto }
}

function Errors({ id, messages }: { id: string; messages?: string[] }) {
  if (!messages?.length) return null
  return (
    <>
      {messages.map((m, i) => (
        <p key={i} id={i === 0 ? id : undefined} className={styles.error} role="alert">{m}</p>
      ))}
    </>
  )
}

export default function SubmitPage() {
  const navigate = useNavigate()
  const toast = useToast()
  const [searchParams, setSearchParams] = useSearchParams()
  const from = searchParams.get('from')
  const [last] = useState(loadLastOptions)
  const nextId = useRef(1)
  const [orgs, setOrgs] = useState<OrgDto[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [templates, setTemplates] = useState<TemplateSummaryDto[]>([])
  const [image, setImage] = useState(last.image ?? '')
  const [orgId, setOrgId] = useState('')
  const [files, setFiles] = useState<Entry[]>([])
  const [url, setUrl] = useState('')
  const [networkMode, setNetworkMode] = useState<NetworkMode>(
    last.networkMode && SUBMITTABLE_MODES.includes(last.networkMode) ? last.networkMode : 'Simulated',
  )
  const [exitCountry, setExitCountry] = useState(last.exitCountry ?? '')
  const [durationSec, setDurationSec] = useState(last.durationSec ?? '300')
  const [interactive, setInteractive] = useState(last.interactive ?? true)
  const [loaded, setLoaded] = useState<{ id: string; task: TaskDto } | null>(null)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    let cancelled = false
    listOrgs().then(
      (o) => {
        if (cancelled) return
        setOrgs(o)
        // A remembered organization that no longer exists falls back to none.
        setOrgId((prev) => prev || (last.orgId && o.some((x) => x.id === last.orgId) ? last.orgId : ''))
      },
      (e: unknown) => !cancelled && setLoadError(e instanceof Error ? e.message : 'Could not load organizations.'),
    )
    listTemplates().then(
      (t) => {
        if (cancelled) return
        setTemplates(t)
        // A remembered or prefilled template that is no longer listed falls back to the default.
        setImage((prev) => (t.some((x) => x.id === prev) ? prev : ((t.find((x) => x.isDefault) ?? t[0])?.id ?? '')))
      },
      (e: unknown) => !cancelled && setLoadError(e instanceof Error ? e.message : 'Could not load templates.'),
    )
    return () => {
      cancelled = true
    }
  }, [last])

  // Resubmit mode: /submit?from=<taskId> loads the source task and prefills its options.
  useEffect(() => {
    if (!from) return
    let cancelled = false
    getTask(from).then(
      (t) => {
        if (cancelled) return
        setLoaded({ id: from, task: t })
        setOrgId(t.orgId)
        setImage(t.image)
        if (SUBMITTABLE_MODES.includes(t.networkMode)) setNetworkMode(t.networkMode)
        setExitCountry(t.exitCountry ?? '')
        setDurationSec(String(t.durationSec))
        setInteractive(t.interactive)
      },
      (e: unknown) => {
        if (cancelled) return
        setFormError(messageOf(e).join(' '))
        setSearchParams({}, { replace: true })
      },
    )
    return () => {
      cancelled = true
    }
  }, [from, setSearchParams])
  // While the source task loads, `source` is 'loading'; outside resubmit mode it is null.
  const source: TaskDto | 'loading' | null = !from ? null : loaded?.id === from ? loaded.task : 'loading'
  const resubmitting = source !== null

  // A single enabled template is the only choice, so the picker (and the image field) stay out of the way.
  const pickTemplate = templates.length > 1

  function validate(): FieldErrors {
    const e: FieldErrors = {}
    if (!orgId) e.orgId = ['Select an organization.']
    const trimmed = url.trim()
    if (!resubmitting) {
      const hasFile = files.length > 0
      if (!hasFile && !trimmed) e.source = ['Provide a file or a url.']
      else if (hasFile && trimmed) e.source = ['Provide either a file or a url, not both.']
      else if (trimmed) {
        const u = urlError(trimmed)
        if (u) e.url = [u]
      }
    }
    const d = Number(durationSec)
    if (!Number.isInteger(d) || d < 60 || d > 1800) e.durationSec = ['Duration must be a whole number between 60 and 1800 seconds.']
    if (networkMode === 'LiveVpn' && !exitCountry.trim()) e.exitCountry = ['Enter an exit country for LiveVpn.']
    return e
  }

  function persist() {
    saveLastOptions({ orgId, image, networkMode, exitCountry, durationSec, interactive })
  }

  function buildForm(file: File | null): FormData {
    const form = new FormData()
    form.set('orgId', orgId)
    if (file) form.set('file', file)
    else form.set('url', url.trim())
    form.set('networkMode', networkMode)
    form.set('durationSec', String(Number(durationSec)))
    form.set('interactive', String(interactive))
    if (pickTemplate && image) form.set('image', image)
    if (networkMode === 'LiveVpn') form.set('exitCountry', exitCountry.trim())
    return form
  }

  function showSingleError(e: unknown) {
    if (e instanceof ApiError) {
      const problem = (e.problem ?? {}) as ProblemDetails
      if (problem.errors && typeof problem.errors === 'object') setErrors(problem.errors)
      else setFormError(problem.title ?? e.message)
    } else {
      setFormError(e instanceof Error ? e.message : 'Submission failed.')
    }
  }

  function leaveResubmit() {
    setLoaded(null)
    setSearchParams({}, { replace: true })
  }

  async function runResubmit(id: string) {
    try {
      const task = await resubmitTask(id, {
        orgId,
        image: image || undefined,
        networkMode,
        exitCountry: networkMode === 'LiveVpn' ? exitCountry.trim() : undefined,
        durationSec: Number(durationSec),
        interactive,
      })
      persist()
      navigate(`/tasks/${task.id}`)
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && (e.problem as { type?: string } | undefined)?.type === 'sample-unavailable') {
        toast.error('This sample has expired under retention. Upload it again.')
        leaveResubmit()
      } else {
        showSingleError(e)
      }
    }
  }

  const patch = (id: number, p: Partial<Entry>) => setFiles((fs) => fs.map((f) => (f.id === id ? { ...f, ...p } : f)))

  async function runBatch() {
    // One file or one URL behaves as it always did: errors land on the form and success opens the task.
    if (url.trim() || files.length === 1) {
      const entry = files[0]
      if (entry) patch(entry.id, { state: 'uploading' })
      try {
        const task = await submitTask(buildForm(entry?.file ?? null))
        persist()
        navigate(`/tasks/${task.id}`)
      } catch (e) {
        if (entry) patch(entry.id, { state: 'idle' })
        showSingleError(e)
      }
      return
    }

    const todo = files.filter((f) => f.state !== 'created')
    setFiles((fs) => fs.map((f) => (f.state === 'created' ? f : { ...f, state: 'queued', error: undefined })))
    let created = 0
    for (const entry of todo) {
      patch(entry.id, { state: 'uploading' })
      try {
        const task = await submitTask(buildForm(entry.file))
        patch(entry.id, { state: 'created', taskId: task.id })
        created++
        persist()
      } catch (e) {
        patch(entry.id, { state: 'failed', error: messageOf(e).join(' ') })
      }
    }
    if (created > 0) toast.success(`Created ${created} ${created === 1 ? 'task' : 'tasks'}`)
  }

  async function onSubmit(ev: FormEvent) {
    ev.preventDefault()
    setFormError(null)
    const local = validate()
    setErrors(local)
    if (Object.keys(local).length > 0) return

    setSubmitting(true)
    if (resubmitting && from) await runResubmit(from)
    else await runBatch()
    setSubmitting(false)
  }

  async function checkDuplicate(entry: Entry) {
    const sha = await sha256Hex(entry.file)
    if (!sha) return
    try {
      const res = await listTasks({ q: sha, take: 1 })
      if (res.total > 0 && res.items[0]) patch(entry.id, { dup: { total: res.total, latest: res.items[0] } })
    } catch {
      // informational only: a failed lookup never blocks a submission
    }
  }

  function addFiles(picked: File[]) {
    if (picked.length === 0) return
    const entries: Entry[] = picked.map((file) => ({ id: nextId.current++, file, state: 'idle' }))
    setFiles((fs) => [...fs, ...entries])
    for (const entry of entries) void checkDuplicate(entry)
  }

  function rowStatus(f: Entry) {
    switch (f.state) {
      case 'queued':
        return 'Queued for upload'
      case 'uploading':
        return 'Uploading'
      case 'created':
        return (
          <>
            <span>Created</span> <Link to={`/tasks/${f.taskId}`}>Open task</Link>
          </>
        )
      case 'failed':
        return <span className={dropStyles.failed}>{`Failed: ${f.error ?? 'Submission failed.'}`}</span>
      default:
        return undefined
    }
  }

  const items: DropItem[] = files.map((f) => ({
    id: f.id,
    file: f.file,
    status: rowStatus(f),
    extra: f.dup && (
      <>
        Analysed {f.dup.total} {f.dup.total === 1 ? 'time' : 'times'} before — latest: <VerdictBadge verdict={f.dup.latest.verdict} />,{' '}
        <RelativeTime iso={f.dup.latest.createdAt} /> · <Link to={`/tasks/${f.dup.latest.id}`}>View</Link>
      </>
    ),
  }))

  // The server reports a missing/duplicate source under both "file" and "url"; show it once, under the source inputs.
  const sourceErrors = errors.source ?? (errors.file && errors.url && errors.file[0] === errors.url[0] ? errors.file : undefined)
  const fileErrors = sourceErrors ? undefined : errors.file
  const urlErrors = sourceErrors ? undefined : errors.url
  const known = new Set(['orgId', 'file', 'url', 'source', 'networkMode', 'exitCountry', 'durationSec', 'interactive', 'image'])
  const otherErrors = Object.entries(errors).filter(([k]) => !known.has(k)).flatMap(([k, v]) => v.map((m) => `${k}: ${m}`))

  return (
    <section>
      <h1>Submit a sample</h1>
      {loadError && <p className={styles.error} role="alert">{loadError}</p>}
      <form className={styles.form} onSubmit={onSubmit} noValidate>
        <div className={styles.field}>
          <label htmlFor="orgId">Organization</label>
          <select id="orgId" value={orgId} onChange={(e) => setOrgId(e.target.value)} aria-describedby="orgId-error">
            <option value="">Choose…</option>
            {(orgs ?? []).map((o) => (
              <option key={o.id} value={o.id}>{o.name}</option>
            ))}
          </select>
          <Errors id="orgId-error" messages={errors.orgId} />
        </div>

        {pickTemplate && (
          <div className={styles.field}>
            <label htmlFor="image">Template</label>
            <select id="image" value={image} onChange={(e) => setImage(e.target.value)} aria-describedby="image-error">
              {templates.map((t) => (
                <option key={t.id} value={t.id}>{t.displayName}</option>
              ))}
            </select>
            <Errors id="image-error" messages={errors.image} />
          </div>
        )}

        {resubmitting ? (
          <div className={styles.field}>
            <span className={styles.legend}>Sample</span>
            <div className={page.chip}>
              <span className={page.chipText}>{source === 'loading' ? 'Loading…' : safeText(source?.originalFileName ?? source?.url)}</span>
              <button type="button" className={styles.secondary} onClick={leaveResubmit}>Change sample</button>
            </div>
          </div>
        ) : (
          <>
            <div className={styles.field}>
              <label htmlFor="file">File</label>
              <DropZone
                items={items}
                disabled={submitting}
                onAdd={addFiles}
                onRemove={(id) => setFiles((fs) => fs.filter((f) => f.id !== id))}
                describedBy="file-error"
              />
              <Errors id="file-error" messages={fileErrors} />
            </div>

            <div className={styles.field}>
              <label htmlFor="url">URL</label>
              <input id="url" type="text" inputMode="url" autoComplete="off" placeholder="https://example.com/…" value={url} onChange={(e) => setUrl(e.target.value)} aria-describedby="url-error" />
              <span className={styles.hint}>Provide either files or a URL.</span>
              <Errors id="url-error" messages={urlErrors} />
              <Errors id="source-error" messages={sourceErrors} />
            </div>
          </>
        )}

        <div className={styles.field}>
          <label htmlFor="networkMode">Network mode</label>
          <select id="networkMode" value={networkMode} onChange={(e) => setNetworkMode(e.target.value as NetworkMode)}>
            {SUBMITTABLE_MODES.map((m) => (
              <option key={m} value={m}>{m}</option>
            ))}
          </select>
          <Errors id="networkMode-error" messages={errors.networkMode} />
        </div>

        {networkMode === 'LiveVpn' && (
          <div className={styles.field}>
            <label htmlFor="exitCountry">Exit country</label>
            <input id="exitCountry" type="text" value={exitCountry} onChange={(e) => setExitCountry(e.target.value)} />
            <Errors id="exitCountry-error" messages={errors.exitCountry} />
          </div>
        )}

        <div className={styles.field}>
          <label htmlFor="durationSec">Duration (seconds)</label>
          <input id="durationSec" type="number" min={60} max={1800} step={1} value={durationSec} onChange={(e) => setDurationSec(e.target.value)} />
          <Errors id="durationSec-error" messages={errors.durationSec} />
        </div>

        <div className={`${styles.field} ${styles.inline}`}>
          <input id="interactive" type="checkbox" checked={interactive} onChange={(e) => setInteractive(e.target.checked)} />
          <label htmlFor="interactive">Interactive (scheduled ahead of automated runs)</label>
          <Errors id="interactive-error" messages={errors.interactive} />
        </div>

        {formError && <p className={styles.error} role="alert">{formError}</p>}
        {otherErrors.map((m, i) => (
          <p key={i} className={styles.error} role="alert">{m}</p>
        ))}

        <div>
          <button type="submit" className={styles.primary} disabled={submitting || source === 'loading'}>
            {submitting ? 'Submitting…' : 'Submit'}
          </button>
        </div>
      </form>
    </section>
  )
}
