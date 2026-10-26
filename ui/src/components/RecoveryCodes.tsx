import { CopyButton } from '../kit/CopyButton'
import forms from '../pages/forms.module.css'
import styles from './AuthLayout.module.css'

function saveTextFile(fileName: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain' }))
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  a.style.display = 'none'
  document.body.appendChild(a)
  try {
    a.click()
  } finally {
    a.remove()
    setTimeout(() => URL.revokeObjectURL(url), 0)
  }
}

/** Recovery codes, shown once, with copy and download. `onDone` is the acknowledgement. */
export function RecoveryCodes({ codes, onDone, doneLabel = 'I have saved these codes' }: { codes: string[]; onDone: () => void; doneLabel?: string }) {
  const text = codes.join('\n') + '\n'
  return (
    <div className={styles.section}>
      <h2>Recovery codes</h2>
      <p className={forms.hint}>Each code works once if you lose your authenticator. They are shown only now; store them somewhere safe.</p>
      <ul className={styles.codes}>
        {codes.map((c) => <li key={c}>{c}</li>)}
      </ul>
      <div className={styles.actions}>
        <CopyButton value={text} label="Copy recovery codes" />
        <button type="button" className={forms.secondary} onClick={() => saveTextFile('maldex-recovery-codes.txt', text)}>Download</button>
        <button type="button" className={forms.primary} onClick={onDone}>{doneLabel}</button>
      </div>
    </div>
  )
}
