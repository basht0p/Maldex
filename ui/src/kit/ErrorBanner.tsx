import styles from './kit.module.css'

export function ErrorBanner({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div role="alert" className={styles.errorBanner}>
      <span>{message}</span>
      {onRetry && <button type="button" className={styles.retry} onClick={onRetry}>Retry</button>}
    </div>
  )
}
