import styles from './kit.module.css'

export function Skeleton({ rows = 3 }: { rows?: number }) {
  return (
    <div className={styles.skeleton} aria-busy="true" aria-label="Loading" role="status">
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className={styles.skeletonRow} data-skeleton-row="" />
      ))}
    </div>
  )
}
