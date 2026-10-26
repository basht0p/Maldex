import { useRef, useState } from 'react'
import { createUser } from '../api/client'
import type { UserDto, UserRole } from '../api/types'
import { useDialogFocus } from './dialogFocus'
import styles from './forms.module.css'
import page from './templates/JobDialog.module.css'
import { userErrorText } from './userErrors'

const USER_NAME_PATTERN = /^[A-Za-z0-9._@-]{1,64}$/
export const MIN_PASSWORD = 12
export const MAX_PASSWORD = 256

const USER_NAME_HINT = 'User names may use letters, digits and . _ @ - (1 to 64 characters).'
export const PASSWORD_HINT = 'Use at least 12 characters.'

/** Create a local account. Checks the name and password shape before calling the API; server errors show inside the dialog. */
export default function NewUserDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (user: UserDto) => void }) {
  const [userName, setUserName] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [role, setRole] = useState<UserRole>('Analyst')
  const [password, setPassword] = useState('')
  const [mustChange, setMustChange] = useState(true)
  const [breakGlass, setBreakGlass] = useState(false)
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const dialogRef = useRef<HTMLDivElement>(null)
  const onKeyDown = useDialogFocus(dialogRef, busy, onClose)

  async function create() {
    const name = userName.trim()
    const problems: string[] = []
    if (!USER_NAME_PATTERN.test(name)) problems.push(USER_NAME_HINT)
    if (password.length < MIN_PASSWORD) problems.push(PASSWORD_HINT)
    else if (password.length > MAX_PASSWORD) problems.push('Use at most 256 characters.')
    setErrors(problems)
    if (problems.length > 0) return
    setBusy(true)
    try {
      onCreated(await createUser({
        userName: name, displayName: displayName.trim() || name, role, password, mustChangePassword: mustChange, isBreakGlass: breakGlass && role === 'Admin',
      }))
    } catch (e) {
      setErrors(userErrorText(e))
      setBusy(false)
    }
  }

  return (
    <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby="new-user-title" className={page.dialog} onKeyDown={onKeyDown}>
      <h3 id="new-user-title">New user</h3>
      <div className={styles.field}>
        <label htmlFor="nu-name">User name</label>
        <input id="nu-name" type="text" autoComplete="off" value={userName} onChange={(e) => setUserName(e.target.value)} />
      </div>
      <div className={styles.field}>
        <label htmlFor="nu-display">Display name</label>
        <input id="nu-display" type="text" autoComplete="off" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
      </div>
      <div className={styles.field}>
        <label htmlFor="nu-role">Role</label>
        <select id="nu-role" value={role} onChange={(e) => setRole(e.target.value as UserRole)}>
          <option value="Analyst">Analyst</option>
          <option value="Admin">Admin</option>
        </select>
      </div>
      <div className={styles.field}>
        <label htmlFor="nu-password">Password</label>
        <input id="nu-password" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        <span className={styles.hint}>12 to 256 characters.</span>
      </div>
      <label className={styles.inline}><input type="checkbox" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} />They must change it at next sign-in</label>
      {role === 'Admin' && (
        <label className={styles.inline}><input type="checkbox" checked={breakGlass} onChange={(e) => setBreakGlass(e.target.checked)} />Break-glass account</label>
      )}
      {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
      <div className={page.dialogActions}>
        <button type="button" className={styles.primary} disabled={busy} onClick={() => void create()}>Create user</button>
        <button type="button" className={styles.secondary} disabled={busy} onClick={onClose}>Cancel</button>
      </div>
    </div>
  )
}
