import { useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import { logout } from '../api/client'

/** Ends the session on the server (a failure is ignored: the cookie may already be gone) and routes to /login. */
export function useSignOut(): () => Promise<void> {
  const navigate = useNavigate()
  return useCallback(async () => {
    try {
      await logout()
    } catch {
      // Already signed out, or the server is unreachable: either way, leave.
    }
    navigate('/login')
  }, [navigate])
}
