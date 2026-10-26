import QRCode from 'qrcode'
import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, totpConfirm, totpSetup } from '../api/client'
import type { AuthNext, TotpSetupDto } from '../api/types'
import { CopyButton } from '../kit/CopyButton'
import forms from '../pages/forms.module.css'
import { messageOf } from '../util/apiMessage'
import styles from './AuthLayout.module.css'
import { RecoveryCodes } from './RecoveryCodes'

type Phase =
  | { kind: 'loading' }
  | { kind: 'setup'; setup: TotpSetupDto; qr: string }
  | { kind: 'codes'; codes: string[]; next: AuthNext }

/**
 * Authenticator enrolment: QR code and key, a confirming code, then the recovery codes once.
 * `onComplete` runs after the recovery codes are acknowledged, with the server's next step.
 */
export function TotpEnrol({ onComplete }: { onComplete: (next: AuthNext) => void }) {
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' })
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false
    ;(async () => {
      try {
        const setup = await totpSetup()
        const qr = await QRCode.toDataURL(setup.otpauthUri)
        if (!cancelled) setPhase({ kind: 'setup', setup, qr })
      } catch (e) {
        if (!cancelled) setError(messageOf(e).join(' '))
      }
    })()
    return () => { cancelled = true }
  }, [])

  async function confirm(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const r = await totpConfirm(code.trim())
      setPhase({ kind: 'codes', codes: r.recoveryCodes, next: r.next })
    } catch (err) {
      setError(err instanceof ApiError && err.code === 'invalid' ? 'That code is not valid.' : messageOf(err).join(' '))
    } finally {
      setBusy(false)
    }
  }

  if (phase.kind === 'codes') return <RecoveryCodes codes={phase.codes} onDone={() => onComplete(phase.next)} />

  return (
    <div className={styles.section}>
      <h2>Set up your authenticator</h2>
      {error && phase.kind === 'loading' && <p className={forms.error} role="alert">{error}</p>}
      {phase.kind === 'loading' && !error && <p className={forms.hint}>Preparing...</p>}
      {phase.kind === 'setup' && (
        <form className={forms.form} onSubmit={(e) => void confirm(e)}>
          <p className={forms.hint}>Scan the QR code with an authenticator app, or enter the key by hand. Then type the 6-digit code it shows.</p>
          <img className={styles.qr} src={phase.qr} alt="Authenticator QR code" />
          <p className={forms.inline}>
            <code className={forms.mono}>{phase.setup.sharedKey}</code>
            <CopyButton value={phase.setup.sharedKey} label="Copy key" />
          </p>
          <div className={forms.field}>
            <label htmlFor="totp-confirm-code">Authentication code</label>
            <input id="totp-confirm-code" type="text" inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(e) => setCode(e.target.value)} required />
          </div>
          {error && <p className={forms.error} role="alert">{error}</p>}
          <div><button type="submit" className={forms.primary} disabled={busy || code.trim() === ''}>Confirm</button></div>
        </form>
      )}
    </div>
  )
}
