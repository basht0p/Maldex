import { type FormEvent, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ApiError, listOrgs, submitTask } from '../api/client'
import type { NetworkMode, OrgDto, ProblemDetails } from '../api/types'
import styles from './forms.module.css'

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
  const [orgs, setOrgs] = useState<OrgDto[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [orgId, setOrgId] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [url, setUrl] = useState('')
  const [networkMode, setNetworkMode] = useState<NetworkMode>('Simulated')
  const [exitCountry, setExitCountry] = useState('')
  const [durationSec, setDurationSec] = useState('300')
  const [interactive, setInteractive] = useState(true)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    let cancelled = false
    listOrgs().then(
      (o) => !cancelled && setOrgs(o),
      (e: unknown) => !cancelled && setLoadError(e instanceof Error ? e.message : 'Could not load organizations.'),
    )
    return () => {
      cancelled = true
    }
  }, [])

  function validate(): FieldErrors {
    const e: FieldErrors = {}
    if (!orgId) e.orgId = ['Select an organization.']
    const trimmed = url.trim()
    if (!file && !trimmed) e.source = ['Provide a file or a url.']
    else if (file && trimmed) e.source = ['Provide either a file or a url, not both.']
    else if (trimmed) {
      const u = urlError(trimmed)
      if (u) e.url = [u]
    }
    const d = Number(durationSec)
    if (!Number.isInteger(d) || d < 60 || d > 1800) e.durationSec = ['Duration must be a whole number between 60 and 1800 seconds.']
    if (networkMode === 'LiveVpn' && !exitCountry.trim()) e.exitCountry = ['Enter an exit country for LiveVpn.']
    return e
  }

  async function onSubmit(ev: FormEvent) {
    ev.preventDefault()
    setFormError(null)
    const local = validate()
    setErrors(local)
    if (Object.keys(local).length > 0) return

    const form = new FormData()
    form.set('orgId', orgId)
    if (file) form.set('file', file)
    else form.set('url', url.trim())
    form.set('networkMode', networkMode)
    form.set('durationSec', String(Number(durationSec)))
    form.set('interactive', String(interactive))
    if (networkMode === 'LiveVpn') form.set('exitCountry', exitCountry.trim())

    setSubmitting(true)
    try {
      const task = await submitTask(form)
      navigate(`/tasks/${task.id}`)
    } catch (e) {
      if (e instanceof ApiError) {
        const problem = (e.problem ?? {}) as ProblemDetails
        if (problem.errors && typeof problem.errors === 'object') setErrors(problem.errors)
        else setFormError(problem.title ?? e.message)
      } else {
        setFormError(e instanceof Error ? e.message : 'Submission failed.')
      }
      setSubmitting(false)
    }
  }

  // The server reports a missing/duplicate source under both "file" and "url"; show it once, under the source inputs.
  const sourceErrors = errors.source ?? (errors.file && errors.url && errors.file[0] === errors.url[0] ? errors.file : undefined)
  const fileErrors = sourceErrors ? undefined : errors.file
  const urlErrors = sourceErrors ? undefined : errors.url
  const known = new Set(['orgId', 'file', 'url', 'source', 'networkMode', 'exitCountry', 'durationSec', 'interactive'])
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

        <div className={styles.field}>
          <label htmlFor="file">File</label>
          <input id="file" type="file" onChange={(e) => setFile(e.target.files?.[0] ?? null)} aria-describedby="file-error" />
          <Errors id="file-error" messages={fileErrors} />
        </div>

        <div className={styles.field}>
          <label htmlFor="url">URL</label>
          <input id="url" type="text" inputMode="url" autoComplete="off" placeholder="https://example.com/…" value={url} onChange={(e) => setUrl(e.target.value)} aria-describedby="url-error" />
          <span className={styles.hint}>Provide exactly one of a file or a URL.</span>
          <Errors id="url-error" messages={urlErrors} />
          <Errors id="source-error" messages={sourceErrors} />
        </div>

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
          <button type="submit" className={styles.primary} disabled={submitting}>
            {submitting ? 'Submitting…' : 'Submit'}
          </button>
        </div>
      </form>
    </section>
  )
}
