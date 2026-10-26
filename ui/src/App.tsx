import { useCallback, useEffect, useRef, useState } from 'react'
import { BrowserRouter, Link, NavLink, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { ApiError, getMe } from './api/client'
import type { MeDto } from './api/types'
import styles from './App.module.css'
import markDark from '../../brand/logo/maldex-mark.svg'
import markLight from '../../brand/logo/maldex-mark-light.svg'
import { onBlocked, onNoRole, onUnauthorized, safeReturnUrl } from './auth/session'
import { useSignOut } from './components/useSignOut'
import { ConfirmProvider } from './kit/Confirm'
import { ShortcutHelp } from './kit/ShortcutHelp'
import { ThemeSwitch } from './kit/ThemeSwitch'
import { ToastProvider } from './kit/Toaster'
import { applyTheme, loadTheme } from './kit/theme'
import { useShortcuts } from './kit/useShortcuts'
import { useToast } from './kit/useToast'
import { SystemStatusProvider } from './live/SystemStatusProvider'
import { useSystemStatus } from './live/useSystemStatus'
import AccountPage from './pages/AccountPage'
import LoginPage from './pages/LoginPage'
import MediaPage from './pages/MediaPage'
import OrgsAdminPage from './pages/OrgsAdminPage'
import StatusPage from './pages/StatusPage'
import SubmitPage from './pages/SubmitPage'
import TaskPage from './pages/TaskPage'
import TemplateDetailPage from './pages/TemplateDetailPage'
import TasksPage from './pages/TasksPage'
import TemplateJobPage from './pages/TemplateJobPage'
import TemplatesAdminPage from './pages/TemplatesAdminPage'
import WaitingPage from './pages/WaitingPage'

applyTheme(loadTheme())

const NARROW = '(max-width: 767px)'

function useNarrow(): boolean {
  const query = () => (typeof window.matchMedia === 'function' ? window.matchMedia(NARROW) : null)
  const [narrow, setNarrow] = useState(() => query()?.matches ?? false)
  useEffect(() => {
    const mq = query()
    if (!mq) return
    const on = () => setNarrow(mq.matches)
    on()
    mq.addEventListener?.('change', on)
    return () => mq.removeEventListener?.('change', on)
  }, [])
  return narrow
}

const ADMIN_LINKS = [
  { to: '/admin/templates', label: 'Templates' },
  { to: '/admin/media', label: 'Media' },
  { to: '/admin/orgs', label: 'Organizations' },
  { to: '/admin/status', label: 'Status' },
]

function AdminLinks() {
  const { attention } = useSystemStatus()
  return (
    <>
      {ADMIN_LINKS.map((l) => (
        <NavLink key={l.label} to={l.to}>
          {l.label}
          {l.label === 'Status' && attention && <AttentionDot />}
        </NavLink>
      ))}
    </>
  )
}

function AttentionDot() {
  return <span className={styles.attention} role="img" aria-label="needs attention" />
}

function AdminMenu({ narrow }: { narrow: boolean }) {
  const { attention } = useSystemStatus()
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false) }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => { document.removeEventListener('mousedown', onDown); document.removeEventListener('keydown', onKey) }
  }, [open])

  if (narrow) {
    return <AdminLinks />
  }
  return (
    <div ref={ref} className={styles.menu}>
      <button type="button" className={styles.menuButton} aria-haspopup="true" aria-expanded={open} onClick={() => setOpen((o) => !o)}>Admin{attention && <AttentionDot />}</button>
      {open && (
        <div className={styles.menuPanel} onClick={() => setOpen(false)}>
          <AdminLinks />
        </div>
      )}
    </div>
  )
}

function UserMenu({ me }: { me: MeDto }) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  const signOut = useSignOut()
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false) }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => { document.removeEventListener('mousedown', onDown); document.removeEventListener('keydown', onKey) }
  }, [open])
  return (
    <div ref={ref} className={styles.menu}>
      <button type="button" className={styles.menuButton} aria-haspopup="true" aria-expanded={open} onClick={() => setOpen((o) => !o)}>{me.displayName} ({me.role})</button>
      {open && (
        <div className={`${styles.menuPanel} ${styles.menuPanelRight}`} onClick={() => setOpen(false)}>
          <Link to="/account">Account</Link>
          <button type="button" className={styles.menuItem} onClick={() => void signOut()}>Sign out</button>
        </div>
      )}
    </div>
  )
}

/** Wires the API client's session hooks to routing and toasts. Renders nothing. */
function SessionBridge() {
  const navigate = useNavigate()
  const toast = useToast()
  useEffect(() => {
    const offs = [
      onUnauthorized(() => {
        const { pathname, search } = window.location
        if (pathname === '/login') return
        const back = safeReturnUrl(pathname + search)
        const target = pathname === '/waiting' || pathname === '/login' ? '/' : back
        navigate(`/login?returnUrl=${encodeURIComponent(target)}`, { replace: true })
      }),
      onNoRole(() => { if (window.location.pathname !== '/waiting') navigate('/waiting', { replace: true }) }),
      onBlocked(() => toast.error('Request blocked; reload the page.')),
    ]
    return () => offs.forEach((off) => off())
  }, [navigate, toast])
  return null
}

function Shell() {
  const [me, setMe] = useState<MeDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [helpOpen, setHelpOpen] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)
  const helpOpener = useRef<HTMLElement | null>(null)
  const narrow = useNarrow()
  const navigate = useNavigate()
  const location = useLocation()

  useEffect(() => {
    getMe().then(
      (m) => {
        if (m.role === null) navigate('/waiting', { replace: true })
        else setMe(m)
      },
      (e: unknown) => {
        // A 401 or no_role is already routed by SessionBridge.
        if (e instanceof ApiError && (e.status === 401 || e.code === 'no_role')) return
        setError(e instanceof Error ? e.message : 'Could not load the current user.')
      },
    )
  }, [navigate])

  const isAdmin = me?.role === 'Admin'

  const openHelp = useCallback(() => {
    helpOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setHelpOpen(true)
  }, [])
  const closeHelp = useCallback(() => {
    setHelpOpen(false)
    const el = helpOpener.current
    setTimeout(() => el?.focus(), 0)
  }, [])

  const shortcuts: Record<string, () => void> = {
    '/': () => document.querySelector<HTMLElement>('[data-search]')?.focus(),
    '?': openHelp,
    Escape: () => setMenuOpen(false),
    'g t': () => navigate('/'),
    'g s': () => navigate('/submit'),
  }
  if (isAdmin) shortcuts['g a'] = () => navigate('/admin/templates')
  useShortcuts(shortcuts)

  const links = (
    <>
      <NavLink to="/" end>Tasks</NavLink>
      <NavLink to="/submit">Submit</NavLink>
      {isAdmin && <AdminMenu narrow={narrow} />}
    </>
  )

  return (
    <SystemStatusProvider enabled={isAdmin && location.pathname.startsWith('/admin')}>
      <nav className={styles.nav} aria-label="Main">
        <span className={styles.brand}>
          <img className={`${styles.mark} ${styles.markDark}`} src={markDark} alt="" />
          <img className={`${styles.mark} ${styles.markLight}`} src={markLight} alt="" />
          <span className={styles.wordmark}>MAL<span>DEX</span></span>
        </span>
        {narrow ? (
          <button type="button" className={styles.menuButton} aria-expanded={menuOpen} onClick={() => setMenuOpen((o) => !o)}>Menu</button>
        ) : links}
        <span className={styles.spacer} />
        {me && <UserMenu me={me} />}
        <ThemeSwitch />
      </nav>
      {narrow && menuOpen && <div className={styles.drawer} onClick={() => setMenuOpen(false)}>{links}</div>}
      <main className={styles.main}>
        {error && <p className={styles.error} role="alert">{error}</p>}
        <Routes>
          <Route path="/" element={<TasksPage />} />
          <Route path="/submit" element={<SubmitPage maxUploadBytes={me?.maxUploadBytes} />} />
          <Route path="/tasks/:id" element={<TaskPage />} />
          <Route path="/admin/orgs" element={<OrgsAdminPage me={me} />} />
          <Route path="/admin/templates" element={<TemplatesAdminPage me={me} />} />
          <Route path="/admin/templates/:id" element={<TemplateDetailPage me={me} />} />
          <Route path="/admin/media" element={<MediaPage />} />
          <Route path="/admin/template-jobs/:id" element={<TemplateJobPage me={me} />} />
          <Route path="/admin/status" element={<StatusPage />} />
          <Route path="*" element={<p>Page not found.</p>} />
        </Routes>
      </main>
      {helpOpen && <ShortcutHelp onClose={closeHelp} />}
    </SystemStatusProvider>
  )
}

export default function App() {
  return (
    <BrowserRouter>
      <ToastProvider>
        <ConfirmProvider>
          <SessionBridge />
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/account" element={<AccountPage />} />
            <Route path="/waiting" element={<WaitingPage />} />
            <Route path="*" element={<Shell />} />
          </Routes>
        </ConfirmProvider>
      </ToastProvider>
    </BrowserRouter>
  )
}
