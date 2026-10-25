import { useCallback, useEffect, useRef, useState } from 'react'
import { deleteMedia, fetchMedia, listMedia } from '../api/client'
import { uploadMedia } from '../api/mediaUpload'
import type { MediaFileDto, MediaKind } from '../api/types'
import { messageOf } from '../util/apiMessage'
import styles from './forms.module.css'
import page from './MediaLibrary.module.css'

/** While any upload is Receiving, the list is polled at this interval (one request at a time). */
const POLL_MS = 2000

const KIND_LABEL: Record<MediaKind, string> = { OsInstaller: 'OS installer', Application: 'Application' }

function sizeText(bytes: number): string {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GiB`
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MiB`
  return `${bytes} B`
}

function statusText(m: MediaFileDto): string {
  if (m.status === 'Failed') return `Failed${m.failureReason ? `: ${m.failureReason}` : ''}`
  if (m.status === 'Ready') return 'Ready'
  if (m.receivedBytes >= m.size) return 'checking' // all bytes arrived; the server is hashing and inspecting
  return `Receiving ${m.size > 0 ? Math.floor((m.receivedBytes * 100) / m.size) : 0}%`
}

/** e.g. `6 · Windows 11 Pro (26300.1)`; an unsupported edition says why. */
function detected(m: MediaFileDto): string[] {
  const lines = m.images.map((i) => `${i.index} · ${i.name}${i.build ? ` (${i.build})` : ''}${i.supported ? '' : ` (not supported${i.unsupportedReason ? `: ${i.unsupportedReason}` : ''})`}`)
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
  const [rows, setRows] = useState<MediaFileDto[]>([])
  const [errors, setErrors] = useState<string[]>([]) // the last action's failure
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
  const mounted = useRef(true)

  const refresh = useCallback(async () => {
    if (inFlight.current) return
    inFlight.current = true
    try {
      const list = await listMedia()
      if (mounted.current) { setRows(list); setLoadError(null) }
    } catch (e) {
      if (mounted.current) setLoadError(messageOf(e)[0])
    } finally {
      inFlight.current = false
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
    const timer = setInterval(() => void refresh(), POLL_MS)
    return () => clearInterval(timer)
  }, [receiving, refresh])

  async function startUpload(f: File, kind: MediaKind, resumeId?: string) {
    const controller = new AbortController()
    abort.current = controller
    setErrors([])
    setUpload({ name: f.name, received: 0, size: f.size })
    try {
      await uploadMedia(f, kind, (received, size) => { if (mounted.current) setUpload({ name: f.name, received, size }) }, controller.signal, resumeId)
    } catch (e) {
      if (mounted.current) setErrors(e instanceof DOMException && e.name === 'AbortError' ? ['The upload was stopped. Resume it from the table.'] : messageOf(e))
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
      setErrors([`Choose the same file: ${row.fileName} (${row.size} bytes). The name and size must match.`])
      return
    }
    void startUpload(f, row.kind, row.id)
  }

  async function remove(m: MediaFileDto) {
    if (!window.confirm(`Delete ${m.fileName}? Its file is removed from the library.`)) return
    try {
      await deleteMedia(m.id)
      setErrors([])
      await refresh()
    } catch (e) {
      setErrors(messageOf(e))
    }
  }

  async function fetchFromUrl(e: React.FormEvent) {
    e.preventDefault()
    try {
      await fetchMedia(url.trim(), fetchKind, fetchName.trim() || undefined)
      setErrors([])
      setUrl('')
      setFetchName('')
      await refresh()
    } catch (err) {
      setErrors(messageOf(err))
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
      {loadError && <p className={styles.error} role="alert">{loadError}</p>}
      {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
      <table className={styles.table} aria-label="Media">
        <thead><tr><th>Kind</th><th>Name</th><th>Size</th><th>SHA-256</th><th>Status</th><th>Detected</th><th>Added</th><th>Used by</th><th>Actions</th></tr></thead>
        <tbody>
          {rows.map((m) => (
            <tr key={m.id}>
              <td>{KIND_LABEL[m.kind]}</td>
              <td className={styles.mono}>{m.fileName}</td>
              <td>{sizeText(m.size)}</td>
              <td className={styles.mono}>{m.sha256 ? m.sha256.slice(0, 12) : ''}</td>
              <td>{statusText(m)}</td>
              <td>{detected(m).map((d) => <div key={d}>{d}</div>)}</td>
              <td>{`${m.addedBy} ${m.addedAt.slice(0, 16).replace('T', ' ')}`}</td>
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
      {rows.length === 0 && <p className={styles.hint}>No media yet. Upload an ISO or fetch one from a URL.</p>}
    </section>
  )
}
