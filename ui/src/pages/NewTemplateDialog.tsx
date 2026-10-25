import { useEffect, useRef, useState } from 'react'
import { listMedia, startTemplateBuild } from '../api/client'
import type { AdminTemplateDto, MediaFileDto, StartBuildRequest, TemplateJobDto } from '../api/types'
import { messageOf } from '../util/apiMessage'
import { useDialogFocus } from './dialogFocus'
import styles from './forms.module.css'
import page from './templates/JobDialog.module.css'

const NEW = ''

/**
 * Start dialog for a Build: a new template (id and name) or a new version of an existing one, from Ready installer media,
 * one of its editions and optionally Office media. Errors, such as a 409 reason, show inside it.
 */
export default function NewTemplateDialog({ templates, onClose, onStarted }: {
  templates: AdminTemplateDto[]
  onClose: () => void
  onStarted: (job: TemplateJobDto) => void
}) {
  const [media, setMedia] = useState<MediaFileDto[] | null>(null)
  const [target, setTarget] = useState(NEW)
  const [templateId, setTemplateId] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [osId, setOsId] = useState('')
  const [edition, setEdition] = useState<number | null>(null) // null = the installer's preferred edition
  const [officeId, setOfficeId] = useState('')
  const [publish, setPublish] = useState(true)
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const dialogRef = useRef<HTMLDivElement>(null)
  const onKeyDown = useDialogFocus(dialogRef, busy, onClose)

  useEffect(() => {
    let cancelled = false
    listMedia().then((list) => { if (!cancelled) setMedia(list) }, (e) => { if (!cancelled) { setMedia([]); setErrors(messageOf(e)) } })
    return () => { cancelled = true }
  }, [])

  const installers = (media ?? []).filter((m) => m.kind === 'OsInstaller' && m.status === 'Ready')
  const offices = (media ?? []).filter((m) => m.kind === 'Application' && m.status === 'Ready' && m.application === 'Office')
  const os = installers.find((m) => m.id === osId) ?? installers[0]
  const editionIndex = os ? (edition !== null && os.images.some((i) => i.index === edition && i.supported) ? edition : os.preferredImageIndex ?? undefined) : undefined
  const isNew = target === NEW
  const ready = !!os && (!isNew || (templateId.trim() !== '' && displayName.trim() !== ''))

  async function start() {
    if (!os) return
    setBusy(true)
    const req: StartBuildRequest = {
      templateId: isNew ? templateId.trim() : target,
      ...(isNew ? { displayName: displayName.trim() } : {}),
      osMediaId: os.id,
      ...(editionIndex !== undefined ? { editionIndex } : {}),
      applicationMediaIds: officeId ? [officeId] : [],
      publishOnSuccess: publish,
    }
    try {
      onStarted(await startTemplateBuild(req))
    } catch (e) {
      setErrors(messageOf(e))
      setBusy(false)
    }
  }

  return (
    <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby="new-template-title" className={page.dialog} onKeyDown={onKeyDown}>
      <h3 id="new-template-title">New template</h3>
      <div className={styles.field}>
        <label htmlFor="nt-target">Template</label>
        <select id="nt-target" value={target} onChange={(e) => setTarget(e.target.value)}>
          <option value={NEW}>New template</option>
          {templates.map((t) => <option key={t.id} value={t.id}>{t.displayName}</option>)}
        </select>
      </div>
      {isNew && (
        <>
          <div className={styles.field}>
            <label htmlFor="nt-id">Template id</label>
            <input id="nt-id" type="text" value={templateId} onChange={(e) => setTemplateId(e.target.value)} />
            <span className={styles.hint}>Lowercase letters, digits and hyphens.</span>
          </div>
          <div className={styles.field}>
            <label htmlFor="nt-name">Display name</label>
            <input id="nt-name" type="text" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          </div>
        </>
      )}
      {media !== null && installers.length === 0 && <p className={styles.hint}>No Ready installer media. Add one in the Media tab first.</p>}
      <div className={styles.field}>
        <label htmlFor="nt-os">OS media</label>
        <select id="nt-os" value={os?.id ?? ''} onChange={(e) => { setOsId(e.target.value); setEdition(null) }}>
          {installers.map((m) => <option key={m.id} value={m.id}>{m.fileName}</option>)}
        </select>
      </div>
      <div className={styles.field}>
        <label htmlFor="nt-edition">Edition</label>
        <select id="nt-edition" value={editionIndex ?? ''} onChange={(e) => setEdition(Number(e.target.value))}>
          {(os?.images ?? []).map((i) => (
            <option key={i.index} value={i.index} disabled={!i.supported}>
              {`${i.index} · ${i.name}${i.build ? ` (${i.build})` : ''}${i.supported ? '' : `: ${i.unsupportedReason ?? 'not supported'}`}`}
            </option>
          ))}
        </select>
      </div>
      <div className={styles.field}>
        <label htmlFor="nt-office">Office media (optional)</label>
        <select id="nt-office" value={officeId} onChange={(e) => setOfficeId(e.target.value)}>
          <option value="">None</option>
          {offices.map((m) => <option key={m.id} value={m.id}>{m.fileName}</option>)}
        </select>
      </div>
      <label className={styles.inline}><input type="checkbox" checked={publish} onChange={(e) => setPublish(e.target.checked)} />Publish when done</label>
      {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
      <div className={page.dialogActions}>
        <button type="button" className={styles.primary} disabled={busy || !ready} onClick={() => void start()}>Start build</button>
        <button type="button" className={styles.secondary} disabled={busy} onClick={onClose}>Cancel</button>
      </div>
    </div>
  )
}
