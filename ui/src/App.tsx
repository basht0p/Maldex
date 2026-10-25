import { useCallback, useEffect, useRef, useState } from 'react'
import { BrowserRouter, NavLink, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { getMe } from './api/client'
import type { MeDto } from './api/types'
import styles from './App.module.css'
import markDark from '../../brand/logo/maldex-mark.svg'
import markLight from '../../brand/logo/maldex-mark-light.svg'
import { ConfirmProvider } from './kit/Confirm'
import { ShortcutHelp } from './kit/ShortcutHelp'
import { ThemeSwitch } from './kit/ThemeSwitch'
import { ToastProvider } from './kit/Toaster'
import { applyTheme, loadTheme } from './kit/theme'
import { useShortcuts } from './kit/useShortcuts'
import { SystemStatusProvider } from './live/SystemStatusProvider'
import { useSystemStatus } from './live/useSystemStatus'
import MediaPage from './pages/MediaPage'
import OrgsAdminPage from './pages/OrgsAdminPage'
import StatusPage from './pages/StatusPage'
import SubmitPage from './pages/SubmitPage'
import TaskPage from './pages/TaskPage'
import TemplateDetailPage from './pages/TemplateDetailPage'
import TasksPage from './pages/TasksPage'
import TemplateJobPage from './pages/TemplateJobPage'
import TemplatesAdminPage from './pages/TemplatesAdminPage'

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
    getMe().then(setMe, (e: unknown) => setError(e instanceof Error ? e.message : 'Could not load the current user.'))
  }, [])

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
        {me && <span className={styles.user}>{me.displayName} ({me.role})</span>}
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
          <Shell />
        </ConfirmProvider>
      </ToastProvider>
    </BrowserRouter>
  )
}
