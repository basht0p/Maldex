import { useCallback, useEffect, useRef, useState } from 'react'
import { clearUserTotp, getAuthSettings, listUsers, putAuthSettings, resetUserPassword, updateUser } from '../api/client'
import type { MeDto, UpdateUserRequest, UserDto } from '../api/types'
import { ErrorBanner } from '../kit/ErrorBanner'
import { RelativeTime } from '../kit/RelativeTime'
import { Skeleton } from '../kit/Skeleton'
import { useConfirm } from '../kit/useConfirm'
import { useToast } from '../kit/useToast'
import { safeText } from '../util/safeText'
import { useDialogFocus } from './dialogFocus'
import styles from './forms.module.css'
import NewUserDialog, { MAX_PASSWORD, MIN_PASSWORD, PASSWORD_HINT } from './NewUserDialog'
import page from './templates/JobDialog.module.css'
import { userErrorText } from './userErrors'
import local from './UsersAdminPage.module.css'

function ResetPasswordDialog({ user, onClose, onDone }: { user: UserDto; onClose: () => void; onDone: () => void }) {
  const [password, setPassword] = useState('')
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  const onKeyDown = useDialogFocus(ref, busy, onClose)

  async function submit() {
    if (password.length < MIN_PASSWORD || password.length > MAX_PASSWORD) {
      setErrors([password.length > MAX_PASSWORD ? 'Use at most 256 characters.' : PASSWORD_HINT])
      return
    }
    setBusy(true)
    try {
      await resetUserPassword(user.id, password)
      onDone()
    } catch (e) {
      setErrors(userErrorText(e))
      setBusy(false)
    }
  }

  return (
    <div ref={ref} role="dialog" aria-modal="true" aria-labelledby="reset-pw-title" className={page.dialog} onKeyDown={onKeyDown}>
      <h3 id="reset-pw-title">Reset password for {safeText(user.userName)}</h3>
      <div className={styles.field}>
        <label htmlFor="rp-password">New password</label>
        <input id="rp-password" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        <span className={styles.hint}>12 to 256 characters. They must change it at next sign-in.</span>
      </div>
      {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
      <div className={page.dialogActions}>
        <button type="button" className={styles.primary} disabled={busy} onClick={() => void submit()}>Reset password</button>
        <button type="button" className={styles.secondary} disabled={busy} onClick={onClose}>Cancel</button>
      </div>
    </div>
  )
}

function UserRow({ user, onChanged, onReset }: { user: UserDto; onChanged: () => Promise<void>; onReset: (u: UserDto) => void }) {
  const toast = useToast()
  const confirm = useConfirm()
  const name = safeText(user.userName)
  const isLocal = user.kind === 'Local'

  async function patch(req: UpdateUserRequest) {
    try {
      await updateUser(user.id, req)
      await onChanged()
    } catch (e) {
      for (const m of userErrorText(e)) toast.error(m)
      await onChanged() // put a rejected select back to the server's value
    }
  }

  async function clearTotp() {
    const ok = await confirm({ title: `Clear TOTP for ${name}?`, body: 'They will be asked to set up an authenticator again at next sign-in.', confirmLabel: 'Clear TOTP', danger: true })
    if (!ok) return
    try {
      await clearUserTotp(user.id)
      toast.success('TOTP cleared')
      await onChanged()
    } catch (e) {
      for (const m of userErrorText(e)) toast.error(m)
    }
  }

  return (
    <tr>
      <td>{name}</td>
      <td>{safeText(user.displayName)}</td>
      <td>{isLocal ? 'Local' : 'SSO'}</td>
      <td>
        <select
          aria-label={`Role of ${name}`}
          value={user.role ?? 'none'}
          disabled={user.roleManagedByIdp}
          onChange={(e) => void patch({ role: e.target.value as 'Admin' | 'Analyst' | 'none' })}
        >
          <option value="Admin">Admin</option>
          <option value="Analyst">Analyst</option>
          <option value="none">No role</option>
        </select>
        {user.roleManagedByIdp && <span className={local.idpNote}>Managed by your identity provider</span>}
      </td>
      <td>
        <div className={local.badges}>
          <span className={`${local.badge} ${user.isDisabled ? local.warn : ''}`}>{user.isDisabled ? 'Disabled' : 'Active'}</span>
          {isLocal && <span className={`${local.badge} ${user.totpEnabled ? '' : local.off}`}>{user.totpEnabled ? 'TOTP on' : 'TOTP off'}</span>}
          {user.isBreakGlass && <span className={`${local.badge} ${local.warn}`}>Break-glass</span>}
        </div>
      </td>
      <td>{user.lastSignInAt ? <RelativeTime iso={user.lastSignInAt} /> : 'Never'}</td>
      <td>
        <div className={local.actions}>
          <button type="button" className={styles.secondary} aria-label={`${user.isDisabled ? 'Enable' : 'Disable'} ${name}`} onClick={() => void patch({ isDisabled: !user.isDisabled })}>
            {user.isDisabled ? 'Enable' : 'Disable'}
          </button>
          {isLocal && (
            <button type="button" className={styles.secondary} aria-label={`Reset password for ${name}`} onClick={() => onReset(user)}>Reset password</button>
          )}
          {isLocal && user.totpEnabled && (
            <button type="button" className={styles.secondary} aria-label={`Clear TOTP for ${name}`} onClick={() => void clearTotp()}>Clear TOTP</button>
          )}
          {isLocal && user.role === 'Admin' && (
            <button type="button" className={styles.secondary} aria-label={`${user.isBreakGlass ? 'Remove break-glass from' : 'Make break-glass'} ${name}`} onClick={() => void patch({ isBreakGlass: !user.isBreakGlass })}>
              {user.isBreakGlass ? 'Remove break-glass' : 'Make break-glass'}
            </button>
          )}
        </div>
      </td>
    </tr>
  )
}

/** Admin-only. Non-admins see a notice; the API enforces the same rule server-side. */
export default function UsersAdminPage({ me }: { me: MeDto | null }) {
  const toast = useToast()
  const [loaded, setLoaded] = useState(false)
  const [users, setUsers] = useState<UserDto[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)
  const [requireTotp, setRequireTotp] = useState<boolean | null>(null)
  const [savingTotp, setSavingTotp] = useState(false)
  const [creating, setCreating] = useState(false)
  const [resetting, setResetting] = useState<UserDto | null>(null)
  const isAdmin = me?.role === 'Admin'

  const refresh = useCallback(async () => {
    try {
      setUsers(await listUsers())
      setLoadError(null)
    } catch (e) {
      setLoadError(userErrorText(e)[0])
    } finally {
      setLoaded(true)
    }
  }, [])

  useEffect(() => {
    if (!isAdmin) return
    // eslint-disable-next-line react-hooks/set-state-in-effect -- initial fetch
    void refresh()
    getAuthSettings().then((s) => setRequireTotp(s.requireTotpForLocal), (e) => toast.error(userErrorText(e)[0]))
  }, [isAdmin, refresh, toast]) // toast is memoized by ToastProvider, so this runs once per admin session

  if (!me) return <p>Loading…</p>
  if (!isAdmin) return <p role="alert">Administrator access is required to manage users.</p>

  async function toggleTotp(next: boolean) {
    setSavingTotp(true)
    try {
      const saved = await putAuthSettings({ requireTotpForLocal: next })
      setRequireTotp(saved.requireTotpForLocal)
      toast.success(saved.requireTotpForLocal ? 'TOTP is now required for local accounts' : 'TOTP is no longer required for local accounts')
    } catch (e) {
      for (const m of userErrorText(e)) toast.error(m)
    } finally {
      setSavingTotp(false)
    }
  }

  return (
    <section>
      <div className={local.toolbar}>
        <h1>Users</h1>
        <button type="button" className={styles.primary} onClick={() => setCreating(true)} disabled={creating}>New user</button>
      </div>

      <div className={local.settings}>
        <label className={styles.inline}>
          <input
            type="checkbox"
            role="switch"
            checked={requireTotp ?? false}
            disabled={requireTotp === null || savingTotp}
            onChange={(e) => void toggleTotp(e.target.checked)}
          />
          Require TOTP for local accounts
        </label>
      </div>

      {creating && (
        <NewUserDialog
          onClose={() => setCreating(false)}
          onCreated={(u) => { setCreating(false); toast.success(`User ${safeText(u.userName)} created`); void refresh() }}
        />
      )}
      {resetting && (
        <ResetPasswordDialog
          user={resetting}
          onClose={() => setResetting(null)}
          onDone={() => { toast.success(`Password reset for ${safeText(resetting.userName)}. They must change it at next sign-in.`); setResetting(null) }}
        />
      )}

      {loadError && <ErrorBanner message={loadError} onRetry={() => void refresh()} />}
      {!loaded && <Skeleton rows={3} />}
      <div className={styles.tableScroll}>
        <table className={styles.table}>
          <thead><tr><th>User name</th><th>Name</th><th>Kind</th><th>Role</th><th>Status</th><th>Last sign-in</th><th /></tr></thead>
          <tbody>
            {users.map((u) => <UserRow key={u.id} user={u} onChanged={refresh} onReset={setResetting} />)}
          </tbody>
        </table>
      </div>
    </section>
  )
}
