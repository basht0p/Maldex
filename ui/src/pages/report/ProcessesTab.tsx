import { useState } from 'react'
import type { TaskReportDto } from '../../api/types'
import { CopyButton } from '../../kit/CopyButton'
import { EmptyState } from '../../kit/EmptyState'
import { safeText } from '../../util/safeText'
import forms from '../forms.module.css'
import { safeLines } from './reportUtil'
import { TruncatedNotice } from './shared'
import styles from './report.module.css'

/** The file name of a Windows or POSIX path. */
function leaf(path: string): string {
  const i = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'))
  return i >= 0 ? path.slice(i + 1) : path
}

export function ProcessesTab({ report, onShowEvents }: { report: TaskReportDto; onShowEvents: (processId: number) => void }) {
  const [expanded, setExpanded] = useState<ReadonlySet<number>>(new Set())
  const toggle = (id: number) => setExpanded((s) => {
    const next = new Set(s)
    if (!next.delete(id)) next.add(id)
    return next
  })
  if (report.processes.length === 0) return <EmptyState title="No processes" />
  return (
    <>
      <TruncatedNotice show={report.truncated.processes} />
      <ul className={styles.tree}>
        {report.processes.map((p) => {
          const open = expanded.has(p.id)
          const name = safeText(leaf(p.imagePath)) || '(unknown)'
          return (
            <li key={p.id} style={{ paddingLeft: `${p.depth * 1.25}rem` }}>
              <div className={styles.treeRow}>
                <span className={styles.treeName} title={safeText(p.imagePath)}>{name}</span>
                <span className={styles.mono}>PID {p.pid}</span>
                <span className={styles.muted}>{safeText(p.user ?? '')}</span>
                <span className={`${styles.mono} ${styles.cmdline}`}>{safeText(p.commandLine)}</span>
                <button
                  type="button"
                  className={forms.secondary}
                  aria-expanded={open}
                  aria-label={`${open ? 'Collapse' : 'Expand'} ${name} ${p.pid}`}
                  onClick={() => toggle(p.id)}
                >
                  {open ? 'Collapse' : 'Expand'}
                </button>
                <button type="button" className={forms.secondary} onClick={() => onShowEvents(p.id)}>Show events</button>
              </div>
              {open && (
                <div className={styles.treeDetail}>
                  <pre className={styles.detail}>{safeLines(p.commandLine)}</pre>
                  <CopyButton value={p.commandLine} label="Copy command line" />
                </div>
              )}
            </li>
          )
        })}
      </ul>
    </>
  )
}
