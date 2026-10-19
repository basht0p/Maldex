import type { RunStatus, Verdict } from '../api/types'
import styles from './StatusBadge.module.css'

const statusClass: Record<RunStatus, string> = {
  Queued: styles.queued,
  Preparing: styles.active,
  Running: styles.active,
  Collecting: styles.active,
  Processing: styles.active,
  Completed: styles.completed,
  Failed: styles.failed,
  Cancelled: styles.cancelled,
}

const verdictClass: Record<Verdict, string> = {
  Unknown: styles.cancelled,
  NoThreats: styles.completed,
  Suspicious: styles.suspicious,
  Malicious: styles.failed,
}

export function StatusBadge({ status }: { status: RunStatus }) {
  return <span className={`${styles.badge} ${statusClass[status]}`}>{status}</span>
}

export function VerdictBadge({ verdict }: { verdict: Verdict }) {
  const label = verdict === 'NoThreats' ? 'No threats' : verdict
  return <span className={`${styles.badge} ${verdictClass[verdict]}`}>{label}</span>
}

export default StatusBadge
