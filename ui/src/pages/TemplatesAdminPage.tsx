import { useCallback, useEffect, useState } from 'react'
import { ApiError, listAdminTemplates, rollBackTemplate, setDefaultTemplate, setTemplateEnabled } from '../api/client'
import type { AdminTemplateDto, MeDto, ProblemDetails, TemplateVersionDto } from '../api/types'
import styles from './forms.module.css'
import page from './TemplatesAdminPage.module.css'

function messageOf(e: unknown): string[] {
  if (e instanceof ApiError) {
    const p = (e.problem ?? {}) as ProblemDetails
    if (p.errors) return Object.values(p.errors).flat()
    return [p.title ?? e.message]
  }
  return [e instanceof Error ? e.message : 'Request failed.']
}

/** e.g. `v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)`; mirrors the server's task provenance line. */
function versionSummary(v: TemplateVersionDto): string {
  const parts: string[] = []
  const windows = [v.windowsEdition, v.windowsBuild].filter((s) => s && s.trim()).join(' ')
  if (windows) parts.push(windows)
  if (v.agentVersion) parts.push(`agent ${v.agentVersion}`)
  parts.push(`sealed ${v.sealedAt.slice(0, 10)}${v.sealedBy ? ` by ${v.sealedBy}` : ''}`)
  return `v${v.number} (${parts.join(', ')})`
}

/** The versions the server keeps for rollback, rollback target first (its `role` decides; a disk may still be missing). */
function keptVersions(t: AdminTemplateDto): TemplateVersionDto[] {
  const target = t.versions.filter((v) => v.role === 'RollbackTarget')
  return [...target, ...t.versions.filter((v) => v.role === 'Previous')]
}

function officeLabel(present: boolean | null | undefined): string {
  return present == null ? 'Office unknown' : present ? 'Office ✓' : 'Office ✗'
}

function TemplateCard({ template: t, onChanged }: { template: AdminTemplateDto; onChanged: () => Promise<void> }) {
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const kept = keptVersions(t)
  const previous = kept.find((v) => v.role === 'RollbackTarget')
  const pending = t.versions.filter((v) => v.role === 'Pending')
  const previousDiskMissing = previous !== undefined && !previous.diskPresent
  const headingId = `template-${t.id}`

  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    try {
      await action()
      setErrors([])
      await onChanged()
    } catch (e) {
      setErrors(messageOf(e))
    } finally {
      setBusy(false)
    }
  }

  function rollBack() {
    if (!previous) return
    const ok = window.confirm(
      `Roll back ${t.displayName} to v${previous.number}? New tasks will use v${previous.number}.${t.current ? ` v${t.current.number} will be retired: a rolled-back version is never kept.` : ''}`,
    )
    if (ok) void run(() => rollBackTemplate(t.id))
  }

  return (
    <section className={page.card} aria-labelledby={headingId}>
      <div className={page.head}>
        <h2 id={headingId}>{t.displayName}</h2>
        <code className={page.id}>{t.id}</code>
        {t.isDefault && <span className={page.badge}>Default</span>}
        <span className={page.spacer} />
        <label className={page.toggle}>
          <input type="checkbox" checked={t.enabled} disabled={busy} onChange={(e) => void run(() => setTemplateEnabled(t.id, e.target.checked))} />
          Enabled
        </label>
        <button type="button" className={styles.secondary} disabled={busy || t.isDefault} onClick={() => void run(() => setDefaultTemplate(t.id))}>Set as default</button>
        <button type="button" className={styles.secondary} disabled={busy || !previous || previousDiskMissing} title={previousDiskMissing ? `Cannot roll back: the disk is missing for v${previous.number}.` : undefined} onClick={rollBack}>Roll back</button>
      </div>
      {t.description && <p className={styles.hint}>{t.description}</p>}
      {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
      <p className={page.summary}>
        {t.current ? <>
            <span className={styles.mono}>{`Current: ${versionSummary(t.current)}`}</span>{' '}
            <span className={styles.hint}>{officeLabel(t.current.officePresent)}</span>
          </> : <span className={styles.hint}>No current version</span>}
      </p>
      <p className={page.summary}>
        {kept.length > 0 ? <span className={styles.mono}>{`Previous kept: ${kept.map((v) => `v${v.number}`).join(', ')}`}</span> : <span className={styles.hint}>No previous version kept</span>}
      </p>
      {pending.length > 0 && (
        <p className={page.summary}>
          <span className={styles.hint}>{`Not yet published: ${pending.map((v) => `v${v.number}`).join(', ')}`}</span>
        </p>
      )}
      <table className={styles.table}>
        <thead><tr><th>Version</th><th>Origin</th><th>Status</th><th>Disk</th></tr></thead>
        <tbody>
          {t.versions.map((v) => (
            <tr key={v.id}>
              <td className={styles.mono}>v{v.number}{v.isCurrent ? ' (current)' : ''}</td>
              <td>{v.origin}</td>
              <td>{v.status}</td>
              <td>{v.status !== 'Deleted' && !v.diskPresent && <span className={page.warn}>Disk missing</span>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}

/** Admin-only. Non-admins see a notice; the API enforces the same rule server-side. */
export default function TemplatesAdminPage({ me }: { me: MeDto | null }) {
  const [templates, setTemplates] = useState<AdminTemplateDto[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)
  const isAdmin = me?.role === 'Admin'

  const refresh = useCallback(async () => {
    try {
      setTemplates(await listAdminTemplates())
      setLoadError(null)
    } catch (e) {
      setLoadError(messageOf(e)[0])
    }
  }, [])

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- initial fetch
    if (isAdmin) void refresh()
  }, [isAdmin, refresh])

  if (!me) return <p>Loading…</p>
  if (!isAdmin) return <p role="alert">Administrator access is required to manage sandbox templates.</p>

  return (
    <section>
      <h1>Sandbox Templates</h1>
      {loadError && <p className={styles.error} role="alert">{loadError}</p>}
      {templates.map((t) => <TemplateCard key={t.id} template={t} onChanged={refresh} />)}
    </section>
  )
}
