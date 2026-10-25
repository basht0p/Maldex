import { Fragment, useState } from 'react'
import type { DetectionDto, TaskReportDto } from '../../api/types'
import { EmptyState } from '../../kit/EmptyState'
import { SortHeader } from '../../kit/SortHeader'
import { safeText } from '../../util/safeText'
import forms from '../forms.module.css'
import { safeLines, useSorted } from './reportUtil'
import styles from './report.module.css'

const KEYS = {
  severity: (d: DetectionDto) => d.severity,
  rule: (d: DetectionDto) => d.rule.toLowerCase(),
  source: (d: DetectionDto) => d.source,
}

export function DetectionsTab({ report }: { report: TaskReportDto }) {
  const { sorted, sort, dir, onSort } = useSorted(report.detections, KEYS, { sort: 'severity', dir: 'desc' })
  const [open, setOpen] = useState<ReadonlySet<DetectionDto>>(new Set())
  const toggle = (d: DetectionDto) =>
    setOpen((prev) => {
      const next = new Set(prev)
      if (!next.delete(d)) next.add(d)
      return next
    })

  if (report.detections.length === 0) return <EmptyState title="No detections" body="No rule matched this run." />

  return (
    <div className={styles.scroll}>
      <table className={forms.table}>
        <thead>
          <tr>
            <SortHeader field="severity" label="Severity" sort={sort} dir={dir} onSort={onSort} />
            <SortHeader field="rule" label="Rule" sort={sort} dir={dir} onSort={onSort} />
            <SortHeader field="source" label="Source" sort={sort} dir={dir} onSort={onSort} />
            <th scope="col">Target</th>
          </tr>
        </thead>
        <tbody>
          {sorted.map((d, i) => {
            const expanded = open.has(d)
            return (
              <Fragment key={i}>
                <tr>
                  <td><span className={styles.sev}>{d.severity}</span></td>
                  <td>
                    <button type="button" className={`${styles.rowButton} ${styles.mono}`} aria-expanded={expanded} onClick={() => toggle(d)}>
                      {safeText(d.rule)}
                    </button>
                  </td>
                  <td>{d.source}</td>
                  <td className={styles.mono}>{safeText(d.target)}</td>
                </tr>
                {expanded && (
                  <tr>
                    <td colSpan={4}>
                      {d.details ? <pre className={styles.detail}>{safeLines(d.details)}</pre> : <p className={styles.muted}>No details</p>}
                    </td>
                  </tr>
                )}
              </Fragment>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
