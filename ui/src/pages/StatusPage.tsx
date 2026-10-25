import { Link } from 'react-router-dom'
import { EmptyState } from '../kit/EmptyState'
import { ErrorBanner } from '../kit/ErrorBanner'
import { formatFileSize } from '../kit/format'
import { RelativeTime } from '../kit/RelativeTime'
import { Skeleton } from '../kit/Skeleton'
import { useSystemStatus } from '../live/useSystemStatus'
import { safeText } from '../util/safeText'
import styles from './StatusPage.module.css'

const href = (kind: 'task' | 'job', id: string) => (kind === 'task' ? `/tasks/${id}` : `/admin/template-jobs/${id}`)

export default function StatusPage() {
  const { status, error, refresh } = useSystemStatus()

  return (
    <section aria-label="System status">
      <h1>System status</h1>
      {error && <ErrorBanner message={error} onRetry={refresh} />}
      {!status && !error && <Skeleton />}
      {status && (
        <>
          <p className={styles.updated}>Last updated <RelativeTime iso={status.generatedAt} /></p>

          <div className={styles.panel}>
            <h2>VM slot</h2>
            <p>{status.vmSlot.busy} of {status.vmSlot.capacity} in use</p>
            {status.vmSlot.holders.length > 0 && (
              <ul className={styles.holders}>
                {status.vmSlot.holders.map((h) => (
                  <li key={`${h.kind}-${h.id}`}>
                    <Link to={href(h.kind, h.id)}>{safeText(h.label)}</Link> since <RelativeTime iso={h.since} />
                  </li>
                ))}
              </ul>
            )}
          </div>

          <dl className={styles.tiles}>
            <div className={styles.tile}><dt>Queued</dt><dd>{status.queueDepth}</dd></div>
            <div className={styles.tile}><dt>Running</dt><dd>{status.runningTasks}</dd></div>
            <div className={styles.tile}><dt>Active jobs</dt><dd>{status.activeJobs}</dd></div>
          </dl>

          <div className={styles.panel}>
            <h2>Disks</h2>
            {status.disks.map((d) => {
              const frac = d.totalBytes > 0 ? d.freeBytes / d.totalBytes : 0
              const level = frac < 0.05 ? 'red' : frac < 0.1 ? 'amber' : ''
              return (
                <div key={d.name} className={styles.disk} data-level={level || 'ok'}>
                  <div className={styles.diskHead}>
                    <span><strong>{d.name}</strong> <span className={styles.path}>{safeText(d.path)}</span></span>
                    <span>
                      {formatFileSize(d.freeBytes)} free of {formatFileSize(d.totalBytes)}
                      {level && <span className={`${styles.flag} ${styles[level]}`}> {level === 'red' ? 'Critically low' : 'Low'}</span>}
                    </span>
                  </div>
                  <div className={styles.track} role="meter" aria-label={`${d.name} free space`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(frac * 100)}>
                    <div className={`${styles.fill} ${level ? styles[level] : ''}`} style={{ width: `${Math.round(frac * 100)}%` }} />
                  </div>
                </div>
              )
            })}
          </div>

          <div className={styles.panel}>
            <h2>Workers</h2>
            <div className={styles.scroll}>
              <table className={styles.table}>
                <thead><tr><th>Worker</th><th>Last tick</th><th>Expected interval</th><th>State</th></tr></thead>
                <tbody>
                  {status.workers.map((w) => (
                    <tr key={w.name}>
                      <td>{w.name}</td>
                      <td>{w.lastTickAt ? <RelativeTime iso={w.lastTickAt} /> : 'Not yet'}</td>
                      <td>{w.expectedIntervalSec} s</td>
                      <td>{w.stalled && <span className={styles.stalled}>Stalled</span>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          <div className={styles.panel}>
            <h2>Recent failures</h2>
            {status.recentFailures.length === 0 ? (
              <EmptyState title="No recent failures" />
            ) : (
              <div className={styles.scroll}>
                <table className={styles.table}>
                  <thead><tr><th>Kind</th><th>Item</th><th>Reason</th><th>When</th></tr></thead>
                  <tbody>
                    {status.recentFailures.map((f) => (
                      <tr key={`${f.kind}-${f.id}-${f.at}`}>
                        <td>{f.kind === 'task' ? 'Task' : 'Template job'}</td>
                        <td><Link to={href(f.kind, f.id)}>{safeText(f.label)}</Link></td>
                        <td>{safeText(f.reason)}</td>
                        <td><RelativeTime iso={f.at} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}
    </section>
  )
}
