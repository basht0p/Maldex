import { useRef, useState } from 'react'
import { startTemplateEdit, startTemplateRefresh } from '../../api/client'
import type { AdminTemplateDto, TemplateJobDto, TemplateJobNetwork } from '../../api/types'
import { messageOf } from '../../util/apiMessage'
import { useDialogFocus } from '../dialogFocus'
import styles from '../forms.module.css'
import page from '../TemplatesAdminPage.module.css'

/** Start dialog for an Edit (with the network choice) or a Refresh (confirm only). Errors, such as a 409 reason, show inside it. */
export function JobDialog({ kind, template, onClose, onStarted }: {
  kind: 'Edit' | 'Refresh'
  template: AdminTemplateDto
  onClose: () => void
  onStarted: (job: TemplateJobDto) => void
}) {
  const [network, setNetwork] = useState<TemplateJobNetwork>('None')
  const [publish, setPublish] = useState(true)
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const titleId = `job-dialog-${template.id}`
  const dialogRef = useRef<HTMLDivElement>(null)

  const onKeyDown = useDialogFocus(dialogRef, busy, onClose)

  async function start() {
    setBusy(true)
    try {
      onStarted(kind === 'Edit' ? await startTemplateEdit(template.id, network, publish) : await startTemplateRefresh(template.id, publish))
    } catch (e) {
      setErrors(messageOf(e))
      setBusy(false)
    }
  }

  return (
    <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby={titleId} className={page.dialog} onKeyDown={onKeyDown}>
      <h3 id={titleId}>{kind === 'Edit' ? `Edit ${template.displayName}` : `Refresh components of ${template.displayName}`}</h3>
      {kind === 'Edit' ? (
        <fieldset className={page.fieldset}>
          <legend className={styles.legend}>Network</legend>
          <label className={styles.inline}><input type="radio" name={`network-${template.id}`} checked={network === 'None'} onChange={() => setNetwork('None')} />None</label>
          <label className={styles.inline}><input type="radio" name={`network-${template.id}`} checked={network === 'Direct'} onChange={() => setNetwork('Direct')} />Direct: internet on</label>
          {network === 'Direct' && <p className={page.warn}>The working copy can reach the internet while you edit it.</p>}
        </fieldset>
      ) : (
        <p>Starts the working copy, updates Windows and the Office components unattended, then seals and verifies the result. No one works in the guest.</p>
      )}
      <label className={styles.inline}><input type="checkbox" checked={publish} onChange={(e) => setPublish(e.target.checked)} />Publish when done</label>
      {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
      <div className={page.dialogActions}>
        <button type="button" className={styles.primary} disabled={busy} onClick={() => void start()}>{kind === 'Edit' ? 'Start edit' : 'Start refresh'}</button>
        <button type="button" className={styles.secondary} disabled={busy} onClick={onClose}>Cancel</button>
      </div>
    </div>
  )
}

export default JobDialog
