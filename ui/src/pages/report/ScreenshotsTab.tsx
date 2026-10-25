import type { TaskReportDto } from '../../api/types'
import { EmptyState } from '../../kit/EmptyState'
import { formatTime } from './reportUtil'
import { Screenshot } from './shared'
import styles from './report.module.css'

/** Placeholder grid; Task 12 replaces the body. */
export function ScreenshotsTab({ report }: { report: TaskReportDto }) {
  if (report.screenshots.length === 0) return <EmptyState title="No screenshots" />
  return (
    <div className={styles.gallery}>
      {report.screenshots.map((s) => (
        <figure key={s.artifactId}>
          <Screenshot artifactId={s.artifactId} at={s.at} />
          <figcaption><time dateTime={s.at}>{formatTime(s.at)}</time></figcaption>
        </figure>
      ))}
    </div>
  )
}
