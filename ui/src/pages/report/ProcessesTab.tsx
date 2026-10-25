import type { TaskReportDto } from '../../api/types'
import { EmptyState } from '../../kit/EmptyState'
import { safeText } from '../../util/safeText'
import forms from '../forms.module.css'
import styles from './report.module.css'

/** Placeholder list; Task 11 replaces the body with the process tree. */
export function ProcessesTab({ report, onShowEvents }: { report: TaskReportDto; onShowEvents: (processId: number) => void }) {
  if (report.processes.length === 0) return <EmptyState title="No processes" />
  return (
    <ul className={styles.list}>
      {report.processes.map((p) => (
        <li key={p.id} style={{ paddingLeft: `${p.depth * 1.25}rem` }}>
          <span className={styles.mono}>{p.pid}</span>
          <span className={styles.mono}>{safeText(p.commandLine || p.imagePath)}</span>
          <button type="button" className={forms.secondary} onClick={() => onShowEvents(p.id)}>Show events</button>
        </li>
      ))}
    </ul>
  )
}
