import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { ApiError, getMe, regenerateRecoveryCodes, removeTotp } from '../api/client'
import type { MeDto } from '../api/types'
import { AuthLayout } from '../components/AuthLayout'
import styles from '../components/AuthLayout.module.css'
import { PasswordChangeForm } from '../components/PasswordChangeForm'
import { RecoveryCodes } from '../components/RecoveryCodes'
import { TotpEnrol } from '../components/TotpEnrol'
import { useSignOut } from '../components/useSignOut'
import { useToast } from '../kit/useToast'
import { messageOf } from '../util/apiMessage'
import forms from './forms.module.css'

type Mode = 'idle' | 'enrol' | 'remove' | 'regenerate' | { codes: string[] }

const TOTP_ERRORS: Record<string, string> = {
  invalid: 'Wrong password.',
  totp_required: 'Your administrator requires an authenticator for local accounts.',
}

/** Asks for the account password, then runs `action`. */
function PasswordPrompt({ submitLabel, action, onCancel }: { submitLabel: string; action: (password: string) => Promise<void>; onCancel: () => void }) {
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await action(password)
    } catch (err) {
      setError(err instanceof ApiError && err.code && TOTP_ERRORS[err.code] ? TOTP_ERRORS[err.code] : messageOf(err).join(' '))
      setBusy(false)
    }
  }

  return (
    <form className={forms.form} onSubmit={(e) => void submit(e)}>
      <div className={forms.field}>
        <label htmlFor="account-confirm-password">Password</label>
        <input id="account-confirm-password" type="password" autoComplete="current-password" autoFocus value={password} onChange={(e) => setPassword(e.target.value)} required />
        <span className={forms.hint}>Enter your password to continue.</span>
      </div>
      {error && <p className={forms.error} role="alert">{error}</p>}
      <div className={styles.actions}>
        <button type="submit" className={forms.primary} disabled={busy || password === ''}>{submitLabel}</button>
        <button type="button" className={forms.secondary} onClick={onCancel}>Cancel</button>
      </div>
    </form>
  )
}

function Authenticator({ enabled, onChange }: { enabled: boolean; onChange: (enabled: boolean) => void }) {
  const toast = useToast()
  const [mode, setMode] = useState<Mode>('idle')

  if (typeof mode === 'object') return <RecoveryCodes codes={mode.codes} doneLabel="Done" onDone={() => setMode('idle')} />
  if (mode === 'enrol') return <TotpEnrol onComplete={() => { onChange(true); setMode('idle') }} />
  if (mode === 'remove') {
    return (
      <PasswordPrompt
        submitLabel="Remove"
        onCancel={() => setMode('idle')}
        action={async (password) => { await removeTotp(password); onChange(false); setMode('idle'); toast.success('Authenticator removed.') }}
      />
    )
  }
  if (mode === 'regenerate') {
    return (
      <PasswordPrompt
        submitLabel="Generate"
        onCancel={() => setMode('idle')}
        action={async (password) => { setMode({ codes: (await regenerateRecoveryCodes(password)).recoveryCodes }) }}
      />
    )
  }
  return enabled ? (
    <>
      <p>Authenticator is on.</p>
      <div className={styles.actions}>
        <button type="button" className={forms.secondary} onClick={() => setMode('regenerate')}>Regenerate recovery codes</button>
        <button type="button" className={forms.secondary} onClick={() => setMode('remove')}>Remove authenticator</button>
      </div>
    </>
  ) : (
    <>
      <p>Authenticator is off.</p>
      <div><button type="button" className={forms.primary} onClick={() => setMode('enrol')}>Set up authenticator</button></div>
    </>
  )
}

export default function AccountPage() {
  const toast = useToast()
  const signOut = useSignOut()
  const [me, setMe] = useState<MeDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [totpEnabled, setTotpEnabled] = useState(false)

  useEffect(() => {
    let cancelled = false
    getMe().then(
      (m) => { if (!cancelled) { setMe(m); setTotpEnabled(m.totpEnabled) } },
      (e: unknown) => { if (!cancelled) setError(messageOf(e).join(' ')) },
    )
    return () => { cancelled = true }
  }, [])

  return (
    <AuthLayout title="Account" wide>
      <p><Link className={styles.link} to="/">Back to Maldex</Link></p>
      {error && <p className={forms.error} role="alert">{error}</p>}
      {me && (
        <>
          <dl className={styles.details}>
            <dt>Name</dt>
            <dd>{me.displayName}</dd>
            <dt>Sign-in</dt>
            <dd>{me.kind === 'Sso' ? 'Single sign-on' : 'Local account'}</dd>
            <dt>Role</dt>
            <dd>{me.role ?? 'None'}</dd>
          </dl>
          {me.roleManagedByIdp && <p className={forms.hint}>Your role is managed by your identity provider.</p>}

          {me.kind === 'Local' && (
            <>
              <section className={styles.section} aria-label="Password">
                <h2>Password</h2>
                <PasswordChangeForm onChanged={() => toast.success('Password changed.')} />
              </section>
              <section className={styles.section} aria-label="Authenticator">
                <h2>Authenticator</h2>
                <Authenticator enabled={totpEnabled} onChange={setTotpEnabled} />
              </section>
            </>
          )}
          <div><button type="button" className={forms.secondary} onClick={() => void signOut()}>Sign out</button></div>
        </>
      )}
    </AuthLayout>
  )
}
