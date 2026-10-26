import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { getMe } from '../api/client'
import { AuthLayout } from '../components/AuthLayout'
import { useSignOut } from '../components/useSignOut'
import forms from './forms.module.css'

/** True once an administrator has given the account a role. A 401 is routed to the sign-in page by the session bridge. */
const hasRole = () => getMe().then((me) => me.role !== null)

/**
 * Shown to a signed-in account that has no Maldex role yet. It checks /api/me when it opens, when the tab gets focus again
 * and on "Check again", and moves on to the app as soon as an administrator has granted a role.
 */
export default function WaitingPage() {
  const signOut = useSignOut()
  const navigate = useNavigate()
  const [status, setStatus] = useState<string | null>(null)

  useEffect(() => {
    const check = () => { hasRole().then((yes) => { if (yes) navigate('/', { replace: true }) }, () => undefined) }
    check()
    window.addEventListener('focus', check)
    return () => window.removeEventListener('focus', check)
  }, [navigate])

  async function checkAgain() {
    setStatus(null)
    try {
      if (await hasRole()) navigate('/', { replace: true })
      else setStatus('Still no role.')
    } catch {
      setStatus('Could not check. Try again.')
    }
  }

  return (
    <AuthLayout title="Waiting for access">
      <p>Your account has no Maldex role. Ask an administrator.</p>
      <p className={forms.hint} role="status">{status}</p>
      <div className={forms.inline}>
        <button type="button" className={forms.primary} onClick={() => void checkAgain()}>Check again</button>
        <button type="button" className={forms.secondary} onClick={() => void signOut()}>Sign out</button>
      </div>
    </AuthLayout>
  )
}
