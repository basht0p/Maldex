import type { TaskReportDto } from '../../api/types'
import { CopyButton } from '../../kit/CopyButton'
import { EmptyState } from '../../kit/EmptyState'
import { FileSize } from '../../kit/RelativeTime'
import { safeText } from '../../util/safeText'
import forms from '../forms.module.css'
import styles from './report.module.css'

export function FilesTab({ report }: { report: TaskReportDto }) {
  if (report.droppedFiles.length === 0) return <EmptyState title="No dropped files" />
  return (
    <div className={styles.scroll}>
      <table className={forms.table}>
        <thead>
          <tr>
            <th scope="col">Guest path</th>
            <th scope="col">SHA-256</th>
            <th scope="col">Size</th>
            <th scope="col">Download</th>
          </tr>
        </thead>
        <tbody>
          {report.droppedFiles.map((f) => (
            <tr key={f.artifactId}>
              <td className={styles.mono}>{f.guestPath ? safeText(f.guestPath) : <span className={styles.muted}>Unknown</span>}</td>
              <td>
                <span className={styles.facts}>
                  <span className={styles.mono}>{f.sha256}</span>
                  <CopyButton value={f.sha256} label="Copy SHA-256" />
                </span>
              </td>
              <td><FileSize bytes={f.size} /></td>
              <td><a href={`/api/artifacts/${encodeURIComponent(f.artifactId)}/download`}>ZIP</a></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
