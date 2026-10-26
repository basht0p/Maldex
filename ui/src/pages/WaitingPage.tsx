import { AuthLayout } from '../components/AuthLayout'
import { useSignOut } from '../components/useSignOut'
import forms from './forms.module.css'

/** Shown to a signed-in account that has no Maldex role yet. */
export default function WaitingPage() {
  const signOut = useSignOut()
  return (
    <AuthLayout title="Waiting for access">
      <p>Your account has no Maldex role. Ask an administrator.</p>
      <div><button type="button" className={forms.secondary} onClick={() => void signOut()}>Sign out</button></div>
    </AuthLayout>
  )
}
