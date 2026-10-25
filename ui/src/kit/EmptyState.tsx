import type { ReactNode } from 'react'
import styles from './kit.module.css'

export function EmptyState({ title, body, action }: { title: string; body?: string; action?: ReactNode }) {
  return (
    <div className={styles.empty}>
      <p className={styles.emptyTitle}>{title}</p>
      {body && <p className={styles.emptyBody}>{body}</p>}
      {action && <div className={styles.emptyAction}>{action}</div>}
    </div>
  )
}
