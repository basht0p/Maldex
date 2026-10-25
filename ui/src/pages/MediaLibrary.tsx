import { useCallback, useEffect, useRef, useState } from 'react'
import { deleteMedia, fetchMedia, listMedia } from '../api/client'
import { uploadMedia } from '../api/mediaUpload'
import type { MediaFileDto, MediaKind } from '../api/types'
import { useConfirm } from '../kit/useConfirm'
import { CopyButton } from '../kit/CopyButton'
import { EmptyState } from '../kit/EmptyState'
import { ErrorBanner } from '../kit/ErrorBanner'
import { RelativeTime, FileSize } from '../kit/RelativeTime'
import { Skeleton } from '../kit/Skeleton'
import { useToast } from '../kit/useToast'
import { messageOf } from '../util/apiMessage'
import styles from './forms.module.css'
import page from './MediaLibrary.module.css'

/** While any upload is Receiving, the list is polled at this interval (one request at a time). */
const POLL_MS = 2000

const KIND_LABEL: Record<MediaKind, string> = { OsInstaller: 'OS installer', Application: 'Application' }

function statusText(m: MediaFileDto): string {
  if (m.status === 'Failed') return `Failed${m.failureReason ? `: ${m.failureReason}` : ''}`
  if (m.status === 'Ready') return 'Ready'
  if (m.receivedBytes >= m.size) return 'checking' // all bytes arrived; the server is hashing and inspecting
  return `Receiving ${m.size > 0 ? Math.floor((m.receivedBytes * 100) / m.size) : 0}%`
}

/** The server's reason often restates the edition (`Windows 11 Home is not supported: …`); the name is shown once, so only the cause stays. */
function reasonOf(i: MediaFileDto['images'][number]): string {
  const reason = i.unsupportedReason?.trim()
  if (!reason) return 'not supported'
  const lead = `${i.name} is not supported`
  return reason.startsWith(lead) ? reason.slice(lead.length).replace(/^[:\s]+/, '') || 'not supported' : reason
}

/** e.g. `6 · Windows 11 Pro (26300.1)`; an unsupported edition says why. */
function detected(m: MediaFileDto): string[] {
  const lines = m.images.map((i) => `${i.index} · ${i.name}${i.build ? ` (${i.build})` : ''}${i.supported ? '' : `: ${reasonOf(i)}`}`)
  if (m.application) lines.push(m.application)
  return lines
}

function KindSelect({ id, value, onChange }: { id: string; value: MediaKind; onChange: (k: MediaKind) => void }) {
  return (
    <div className={styles.field}>
      <label htmlFor={id}>Kind</label>
      <select id={id} value={value} onChange={(e) => onChange(e.target.value as MediaKind)}>
        <option value="OsInstaller">{KIND_LABEL.OsInstaller}</option>
        <option value="Application">{KIND_LABEL.Application}</option>
      </select>
    </div>
  )
}

/** The media tab of the templates page: installers and application media that Builds use. */
export default function MediaLibrary() {
  const toast = useToast()
  const confirm = useConfirm()
  const [rows, setRows] = useState<MediaFileDto[]>([])
  const [loaded, setLoaded] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [file, setFile] = useState<File | null>(null)
  const [uploadKind, setUploadKind] = useState<MediaKind>('OsInstaller')
  const [upload, setUpload] = useState<{ name: string; received: number; size: number } | null>(null)
  const abort = useRef<AbortController | null>(null)
  const resumeTarget = useRef<MediaFileDto | null>(null)
  const resumeInput = useRef<HTMLInputElement>(null)
  const [url, setUrl] = useState('')
  const [fetchKind, setFetchKind] = useState<MediaKind>('OsInstaller')
  const [fetchName, setFetchName] = useState('')
  const inFlight = useRef(false)
  const again = useRef(false) // a refresh was requested while one was in flight: run one more when it ends
  const mounted = useRef(true)

  const refresh = useCallback(async (poll = false) => {
    if (inFlight.current) { if (!poll) again.current = true; return } // a dropped poll is not queued: the next tick comes anyway
    inFlight.current = true
    try {
      const list = await listMedia()
      if (mounted.current) { setRows(list); setLoadError(null) }
    } catch (e) {
      if (mounted.current) setLoadError(messageOf(e)[0])
    } finally {
      if (mounted.current) setLoaded(true)
      inFlight.current = false
      if (again.current && mounted.current) { again.current = false; void refresh() }
    }
  }, [])

  useEffect(() => {
    mounted.current = true
    // eslint-disable-next-line react-hooks/set-state-in-effect -- initial fetch
    void refresh()
    return () => { mounted.current = false; abort.current?.abort() }
  }, [refresh])

  const receiving = rows.some((r) => r.status === 'Receiving')
  useEffect(() => {
    if (!receiving) return
    const timer = setInterval(() => void refresh(true), POLL_MS)
    return () => clearInterval(timer)
  }, [receiving, refresh])

  async function startUpload(f: File, kind: MediaKind, resumeId?: string) {
    const controller = new AbortController()
    abort.current = controller
    setUpload({ name: f.name, received: 0, size: f.size })
    try {
      await uploadMedia(f, kind, (received, size) => { if (mounted.current) setUpload({ name: f.name, received, size }) }, controller.signal, resumeId)
      if (mounted.current) toast.success(`${f.name} uploaded`)
    } catch (e) {
      if (mounted.current) {
        if (e instanceof DOMException && e.name === 'AbortError') toast.error('The upload was stopped. Resume it from the table.')
        else for (const m of messageOf(e)) toast.error(m)
      }
    } finally {
      abort.current = null
      if (mounted.current) setUpload(null)
    }
    if (mounted.current) await refresh()
  }

  function onResumeFile(e: React.ChangeEvent<HTMLInputElement>) {
    const f = e.target.files?.[0]
    const row = resumeTarget.current
    e.target.value = ''
    if (!f || !row) return
    if (f.name !== row.fileName || f.size !== row.size) {
      toast.error(`Choose the same file: ${row.fileName} (${row.size} bytes). The name and size must match.`)
      return
    }
    void startUpload(f, row.kind, row.id)
  }

  async function remove(m: MediaFileDto) {
    const ok = await confirm({
      title: `Delete ${m.fileName}?`,
      body: 'Its file is removed from the library.',
      confirmLabel: 'Delete',
      danger: true,
    })
    if (!ok) return
    try {
      await deleteMedia(m.id)
      toast.success(`${m.fileName} deleted`)
      await refresh()
    } catch (e) {
      for (const msg of messageOf(e)) toast.error(msg)
    }
  }

  async function fetchFromUrl(e: React.FormEvent) {
    e.preventDefault()
    try {
      await fetchMedia(url.trim(), fetchKind, fetchName.trim() || undefined)
      toast.success('Fetch started')
      setUrl('')
      setFetchName('')
      await refresh()
    } catch (err) {
      for (const m of messageOf(err)) toast.error(m)
    }
  }

  const uploading = upload !== null

  return (
    <section aria-label="Media library">
      <div className={page.forms}>
        <fieldset className={page.group}>
          <legend className={styles.legend}>Upload</legend>
          <div className={styles.field}>
            <label htmlFor="upload-file">File</label>
            <input id="upload-file" type="file" disabled={uploading} onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
          </div>
          <KindSelect id="upload-kind" value={uploadKind} onChange={setUploadKind} />
          <div className={page.actions}>
            <button type="button" className={styles.primary} disabled={uploading || !file} onClick={() => file && void startUpload(file, uploadKind)}>Upload</button>
            {upload && <button type="button" className={styles.secondary} onClick={() => abort.current?.abort()}>Stop</button>}
          </div>
          {upload && <p className={styles.hint} role="status">{`Uploading ${upload.name}: ${upload.size > 0 ? Math.floor((upload.received * 100) / upload.size) : 0}%`}</p>}
        </fieldset>
        <fieldset className={page.group}>
          <legend className={styles.legend}>Fetch from URL</legend>
          <form onSubmit={(e) => void fetchFromUrl(e)} className={page.groupForm}>
            <div className={styles.field}>
              <label htmlFor="fetch-url">URL</label>
              <input id="fetch-url" type="url" value={url} onChange={(e) => setUrl(e.target.value)} />
            </div>
            <KindSelect id="fetch-kind" value={fetchKind} onChange={setFetchKind} />
            <div className={styles.field}>
              <label htmlFor="fetch-name">Name (optional)</label>
              <input id="fetch-name" type="text" value={fetchName} onChange={(e) => setFetchName(e.target.value)} />
            </div>
            <div className={page.actions}>
              <button type="submit" className={styles.primary} disabled={!url.trim()}>Fetch</button>
            </div>
          </form>
        </fieldset>
      </div>
      <input ref={resumeInput} type="file" aria-label="Resume file" style={{ display: 'none' }} onChange={onResumeFile} />
      {loadError && <ErrorBanner message={loadError} onRetry={() => void refresh()} />}
      {!loaded && !loadError && <Skeleton rows={3} />}
      <table className={styles.table} aria-label="Media">
        <thead><tr><th>Kind</th><th>Name</th><th>Size</th><th>SHA-256</th><th>Status</th><th>Detected</th><th>Added</th><th>Used by</th><th>Actions</th></tr></thead>
        <tbody>
          {rows.map((m) => (
            <tr key={m.id}>
              <td>{KIND_LABEL[m.kind]}</td>
              <td className={styles.mono}>{m.fileName}</td>
              <td><FileSize bytes={m.size} /></td>
              <td className={styles.mono}>{m.sha256 ? <>{m.sha256.slice(0, 12)}<CopyButton value={m.sha256} label="Copy SHA-256" /></> : ''}</td>
              <td>{statusText(m)}</td>
              <td>{detected(m).map((d) => <div key={d}>{d}</div>)}</td>
              <td>{m.addedBy} <RelativeTime iso={m.addedAt} /></td>
              <td>{m.usedBy.join(', ')}</td>
              <td>
                <span className={page.actions}>
                  {m.status === 'Receiving' && m.source === 'Upload' && m.receivedBytes < m.size && (
                    <button type="button" className={styles.secondary} disabled={uploading} onClick={() => { resumeTarget.current = m; resumeInput.current?.click() }}>Resume</button>
                  )}
                  <button type="button" className={styles.secondary} onClick={() => void remove(m)}>Delete</button>
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {loaded && rows.length === 0 && <EmptyState title="No media yet." body="Upload an ISO or fetch one from a URL." />}
    </section>
  )
}
