import { useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, changePassword } from '../api/client'
import type { AuthNext } from '../api/types'
import forms from '../pages/forms.module.css'

export const MIN_PASSWORD_LENGTH = 12

/**
 * Change-password form. With `currentPassword` given (the forced change right after sign-in) the current-password
 * field is left out. Length and repeat are checked here before the API is called.
 */
export function PasswordChangeForm({ currentPassword, onChanged }: { currentPassword?: string; onChanged: (next: AuthNext) => void }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [repeat, setRepeat] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (next.length < MIN_PASSWORD_LENGTH) return setError(`The new password must be at least ${MIN_PASSWORD_LENGTH} characters.`)
    if (next !== repeat) return setError('The passwords do not match.')
    setBusy(true)
    setError(null)
    try {
      const r = await changePassword(currentPassword ?? current, next)
      setCurrent('')
      setNext('')
      setRepeat('')
      onChanged(r.next)
    } catch (err) {
      const code = err instanceof ApiError ? err.code : undefined
      setError(code === 'invalid' ? 'The current password is wrong.'
        : code === 'password_too_short' ? `The new password must be at least ${MIN_PASSWORD_LENGTH} characters.`
        : err instanceof Error ? err.message : 'Could not change the password.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className={forms.form} onSubmit={(e) => void submit(e)}>
      {currentPassword === undefined && (
        <div className={forms.field}>
          <label htmlFor="pw-current">Current password</label>
          <input id="pw-current" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
        </div>
      )}
      <div className={forms.field}>
        <label htmlFor="pw-new">New password</label>
        <input id="pw-new" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} required />
        <span className={forms.hint}>At least {MIN_PASSWORD_LENGTH} characters.</span>
      </div>
      <div className={forms.field}>
        <label htmlFor="pw-repeat">Repeat new password</label>
        <input id="pw-repeat" type="password" autoComplete="new-password" value={repeat} onChange={(e) => setRepeat(e.target.value)} required />
      </div>
      {error && <p className={forms.error} role="alert">{error}</p>}
      <div><button type="submit" className={forms.primary} disabled={busy}>Change password</button></div>
    </form>
  )
}
