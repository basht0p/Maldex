import { useState } from 'react'
import { screenshotUrl } from '../../api/client'
import { useAuthedObjectUrl } from '../../api/useAuthedObjectUrl'
import { safeText } from '../../util/safeText'
import { formatTime, TRUNCATED_NOTICE } from './reportUtil'
import styles from './report.module.css'

export function TruncatedNotice({ show }: { show: boolean }) {
  return show ? <p className={styles.notice}>{TRUNCATED_NOTICE}</p> : null
}

/** Warnings recorded while the task's results were processed; nothing when there are none. */
export function ProcessingWarnings({ warnings }: { warnings: readonly string[] | null | undefined }) {
  if (!warnings || warnings.length === 0) return null
  return (
    <section className={styles.warnings} aria-label="Processing warnings">
      <h2>Processing warnings</h2>
      <ul>
        {warnings.map((w, i) => <li key={i}>{safeText(w)}</li>)}
      </ul>
    </section>
  )
}

/** An authenticated image (fetched with the bearer token) that turns into a "Not available" tile when it fails. */
export function AuthedImage({ path, alt, className }: { path: string; alt: string; className: string }) {
  const { url, failed } = useAuthedObjectUrl(path)
  const [broken, setBroken] = useState<string | null>(null)
  if (failed || (url !== null && broken === url)) return <div className={styles.missing}>Not available</div>
  if (!url) return <div className={className} role="img" aria-label={`Loading ${alt}`} aria-busy="true" />
  return <img className={className} src={url} alt={alt} onError={() => setBroken(url)} />
}

/** A screenshot thumbnail; "Not available" when it cannot be loaded. */
export function Screenshot({ artifactId, at }: { artifactId: string; at: string }) {
  return <AuthedImage path={screenshotUrl(artifactId)} alt={`Screenshot at ${formatTime(at)}`} className={styles.shot} />
}
