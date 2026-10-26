import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError, getAuthConfig, login, loginTotp, ssoStartUrl } from '../api/client'
import type { AuthConfigDto, AuthNext } from '../api/types'
import { nav, safeReturnUrl } from '../auth/session'
import { AuthLayout } from '../components/AuthLayout'
import styles from '../components/AuthLayout.module.css'
import { PasswordChangeForm } from '../components/PasswordChangeForm'
import { TotpEnrol } from '../components/TotpEnrol'
import forms from './forms.module.css'

type Step = 'credentials' | 'totp' | 'change-password' | 'enrol-totp'

const LOGIN_ERRORS: Record<string, string> = {
  invalid: 'Wrong user name or password.',
  locked: 'Too many failed attempts. Try again in 15 minutes.',
  disabled: 'This account is disabled.',
  password_login_disabled: 'Password sign-in is turned off. Use single sign-on.',
  rate_limited: 'Too many sign-in attempts. Wait a minute and try again.',
}
const QUERY_ERRORS: Record<string, string> = {
  no_role: 'Your account has no Maldex role. Ask an administrator.',
  sso_failed: 'Single sign-on failed. Try again or contact an administrator.',
  disabled: LOGIN_ERRORS.disabled,
}

const TIMED_OUT = 'Your sign-in timed out; sign in again.'

function loginMessage(e: unknown): string {
  if (e instanceof ApiError) return (e.code && LOGIN_ERRORS[e.code]) || 'Sign-in failed. Try again.'
  return 'Could not reach the server. Try again.'
}

export default function LoginPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const returnUrl = safeReturnUrl(params.get('returnUrl'))
  const showLocal = params.get('local') === '1'
  const queryError = params.get('error')

  const [config, setConfig] = useState<AuthConfigDto | null>(null)
  const [configError, setConfigError] = useState(false)
  const [step, setStep] = useState<Step>('credentials')
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(queryError ? QUERY_ERRORS[queryError] ?? null : null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false
    getAuthConfig().then(
      (c) => { if (!cancelled) setConfig(c) },
      () => { if (!cancelled) setConfigError(true) },
    )
    return () => { cancelled = true }
  }, [])

  /** The partial session of an unfinished sign-in ran out (10 minutes): start over at the credentials. */
  function expired() {
    setStep('credentials')
    setPassword('')
    setCode('')
    setBusy(false)
    setError(TIMED_OUT)
  }

  function proceed(next: AuthNext) {
    setError(null)
    setCode('')
    if (next === 'done') navigate(returnUrl, { replace: true })
    else setStep(next)
  }

  async function run(action: () => Promise<{ next: AuthNext }>) {
    setBusy(true)
    setError(null)
    try {
      proceed((await action()).next)
    } catch (e) {
      if (e instanceof ApiError && e.code === 'unauthenticated' && step !== 'credentials') return expired()
      setError(e instanceof ApiError && e.code === 'invalid' && step === 'totp' ? 'That code is not valid.' : loginMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const submitCredentials = (e: FormEvent) => { e.preventDefault(); void run(() => login(userName.trim(), password)) }
  const submitCode = (e: FormEvent) => { e.preventDefault(); void run(() => loginTotp(code.trim())) }

  const showForm = config !== null && (config.passwordLogin || showLocal)
  const showSso = config?.sso != null && step === 'credentials'

  return (
    <AuthLayout title="Sign in">
      {configError && <p className={forms.error} role="alert">Could not load the sign-in options. Reload the page.</p>}
      {error && <p className={forms.error} role="alert">{error}</p>}

      {step === 'credentials' && config !== null && !showForm && config.sso === null && (
        <p className={forms.error} role="alert">Sign-in is not configured. Ask an administrator.</p>
      )}

      {step === 'credentials' && showForm && (
        <form className={forms.form} onSubmit={submitCredentials}>
          <div className={forms.field}>
            <label htmlFor="login-user">User name</label>
            <input id="login-user" type="text" autoComplete="username" autoFocus value={userName} onChange={(e) => setUserName(e.target.value)} required />
          </div>
          <div className={forms.field}>
            <label htmlFor="login-password">Password</label>
            <input id="login-password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
          </div>
          <div><button type="submit" className={forms.primary} disabled={busy}>Sign in</button></div>
        </form>
      )}

      {step === 'credentials' && showForm && showSso && <div className={styles.divider}>or</div>}
      {showSso && (
        <div>
          <button type="button" className={forms.primary} onClick={() => nav.assign(ssoStartUrl(returnUrl))}>Sign in with {config.sso!.displayName}</button>
        </div>
      )}

      {step === 'totp' && (
        <form className={forms.form} onSubmit={submitCode}>
          <div className={forms.field}>
            <label htmlFor="login-code">Authentication code or recovery code</label>
            <input id="login-code" type="text" autoComplete="one-time-code" autoFocus value={code} onChange={(e) => setCode(e.target.value)} required />
            <span className={forms.hint}>Use the 6-digit code from your authenticator, or one of your recovery codes.</span>
          </div>
          <div><button type="submit" className={forms.primary} disabled={busy || code.trim() === ''}>Verify</button></div>
        </form>
      )}

      {step === 'change-password' && (
        <div className={styles.section}>
          <h2>Choose a new password</h2>
          <p className={forms.hint}>You must change your password before continuing.</p>
          <PasswordChangeForm currentPassword={password} onChanged={proceed} onExpired={expired} />
        </div>
      )}

      {step === 'enrol-totp' && <TotpEnrol onComplete={proceed} onExpired={expired} />}
    </AuthLayout>
  )
}
