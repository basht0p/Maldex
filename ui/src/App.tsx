import { useEffect, useState } from 'react'
import { BrowserRouter, NavLink, Route, Routes } from 'react-router-dom'
import { getMe } from './api/client'
import type { MeDto } from './api/types'
import styles from './App.module.css'
import markDark from '../../brand/logo/maldex-mark.svg'
import markLight from '../../brand/logo/maldex-mark-light.svg'
import OrgsAdminPage from './pages/OrgsAdminPage'
import SubmitPage from './pages/SubmitPage'
import TaskPage from './pages/TaskPage'
import TasksPage from './pages/TasksPage'
import TemplatesAdminPage from './pages/TemplatesAdminPage'

export default function App() {
  const [me, setMe] = useState<MeDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getMe().then(setMe, (e: unknown) => setError(e instanceof Error ? e.message : 'Could not load the current user.'))
  }, [])

  const isAdmin = me?.role === 'Admin'

  return (
    <BrowserRouter>
      <nav className={styles.nav}>
        <span className={styles.brand}>
          <img className={`${styles.mark} ${styles.markDark}`} src={markDark} alt="" />
          <img className={`${styles.mark} ${styles.markLight}`} src={markLight} alt="" />
          <span className={styles.wordmark}>MAL<span>DEX</span></span>
        </span>
        <NavLink to="/" end>Tasks</NavLink>
        <NavLink to="/submit">Submit</NavLink>
        {isAdmin && <NavLink to="/admin/orgs">Organizations</NavLink>}
        {isAdmin && <NavLink to="/admin/templates">Sandbox Templates</NavLink>}
        <span className={styles.spacer} />
        {me && <span className={styles.user}>{me.displayName} ({me.role})</span>}
      </nav>
      <main className={styles.main}>
        {error && <p className={styles.error} role="alert">{error}</p>}
        <Routes>
          <Route path="/" element={<TasksPage />} />
          <Route path="/submit" element={<SubmitPage />} />
          <Route path="/tasks/:id" element={<TaskPage />} />
          <Route path="/admin/orgs" element={<OrgsAdminPage me={me} />} />
          <Route path="/admin/templates" element={<TemplatesAdminPage me={me} />} />
        <Route path="*" element={<p>Page not found.</p>} />
        </Routes>
      </main>
    </BrowserRouter>
  )
}
