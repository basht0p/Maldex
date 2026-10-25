import { useState } from 'react'
import { screenshotUrl } from '../../api/client'
import { formatTime, TRUNCATED_NOTICE } from './reportUtil'
import styles from './report.module.css'

export function TruncatedNotice({ show }: { show: boolean }) {
  return show ? <p className={styles.notice}>{TRUNCATED_NOTICE}</p> : null
}

/** A screenshot that turns into a "Not available" tile when the image fails to load. */
export function Screenshot({ artifactId, at }: { artifactId: string; at: string }) {
  const [failed, setFailed] = useState(false)
  if (failed) return <div className={styles.missing}>Not available</div>
  return (
    <img
      className={styles.shot}
      src={screenshotUrl(artifactId)}
      alt={`Screenshot at ${formatTime(at)}`}
      loading="lazy"
      onError={() => setFailed(true)}
    />
  )
}
