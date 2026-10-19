import { type FormEvent, useCallback, useEffect, useState } from 'react'
import { ApiError, createOrg, listOrgs, updateOrg } from '../api/client'
import type { MeDto, OrgDto, ProblemDetails } from '../api/types'
import styles from './forms.module.css'

function messageOf(e: unknown): string[] {
  if (e instanceof ApiError) {
    const p = (e.problem ?? {}) as ProblemDetails
    if (p.errors) return Object.values(p.errors).flat()
    return [p.title ?? e.message]
  }
  return [e instanceof Error ? e.message : 'Request failed.']
}

function OrgRow({ org, onSaved }: { org: OrgDto; onSaved: () => void }) {
  const [name, setName] = useState(org.name)
  const [days, setDays] = useState(String(org.retentionDays))
  const [errors, setErrors] = useState<string[]>([])
  const dirty = name !== org.name || days !== String(org.retentionDays)

  async function save() {
    try {
      await updateOrg(org.id, { name, retentionDays: Number(days) })
      setErrors([])
      onSaved()
    } catch (e) {
      setErrors(messageOf(e))
    }
  }

  return (
    <tr>
      <td>
        <input type="text" aria-label={`Name of ${org.name}`} value={name} onChange={(e) => setName(e.target.value)} />
        {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
      </td>
      <td><input type="number" aria-label={`Retention days of ${org.name}`} min={1} max={3650} value={days} onChange={(e) => setDays(e.target.value)} /></td>
      <td><button type="button" className={styles.secondary} disabled={!dirty} onClick={() => void save()}>Save</button></td>
    </tr>
  )
}

/** Admin-only. Non-admins see a notice; the API enforces the same rule server-side. */
export default function OrgsAdminPage({ me }: { me: MeDto | null }) {
  const [orgs, setOrgs] = useState<OrgDto[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [days, setDays] = useState('90')
  const [errors, setErrors] = useState<string[]>([])
  const isAdmin = me?.role === 'Admin'

  const refresh = useCallback(async () => {
    try {
      setOrgs(await listOrgs())
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
  if (!isAdmin) return <p role="alert">Administrator access is required to manage organizations.</p>

  async function onCreate(ev: FormEvent) {
    ev.preventDefault()
    try {
      await createOrg({ name, retentionDays: Number(days) })
      setName('')
      setErrors([])
      await refresh()
    } catch (e) {
      setErrors(messageOf(e))
    }
  }

  return (
    <section>
      <h1>Organizations</h1>
      {loadError && <p className={styles.error} role="alert">{loadError}</p>}
      <table className={styles.table}>
        <thead><tr><th>Name</th><th>Retention (days)</th><th /></tr></thead>
        <tbody>
          {orgs.map((o) => <OrgRow key={`${o.id}:${o.name}:${o.retentionDays}`} org={o} onSaved={() => void refresh()} />)}
        </tbody>
      </table>

      <h2>New organization</h2>
      <form className={styles.form} onSubmit={onCreate}>
        <div className={styles.field}>
          <label htmlFor="newName">Name</label>
          <input id="newName" type="text" value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        <div className={styles.field}>
          <label htmlFor="newDays">Retention (days)</label>
          <input id="newDays" type="number" min={1} max={3650} value={days} onChange={(e) => setDays(e.target.value)} />
        </div>
        {errors.map((m, i) => <p key={i} className={styles.error} role="alert">{m}</p>)}
        <div><button type="submit" className={styles.primary}>Create</button></div>
      </form>
    </section>
  )
}
