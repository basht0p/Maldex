import { Fragment, useState } from 'react'
import { httpBodyUrl } from '../../api/client'
import type { TaskReportDto } from '../../api/types'
import { CopyButton } from '../../kit/CopyButton'
import { EmptyState } from '../../kit/EmptyState'
import { useDownload } from '../../kit/useDownload'
import { safeText } from '../../util/safeText'
import forms from '../forms.module.css'
import { safeLines } from './reportUtil'
import { TruncatedNotice } from './shared'
import styles from './report.module.css'

/** Path and query of a URL; the raw URL when it does not parse. */
function pathOf(url: string): string {
  try {
    const u = new URL(url)
    return u.pathname + u.search
  } catch {
    return url
  }
}

export function HttpTab({ report, taskId }: { report: TaskReportDto; taskId: string }) {
  const [open, setOpen] = useState<ReadonlySet<number>>(new Set())
  const download = useDownload()
  const toggle = (id: number) =>
    setOpen((prev) => {
      const next = new Set(prev)
      if (!next.delete(id)) next.add(id)
      return next
    })

  return (
    <div>
      <TruncatedNotice show={report.truncated.http} />
      {report.http.length === 0 ? (
        <EmptyState title="No HTTP transactions" />
      ) : (
        <div className={styles.scroll}>
          <table className={forms.table}>
            <thead>
              <tr>
                <th scope="col">Method</th>
                <th scope="col">Host</th>
                <th scope="col">Path</th>
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              {report.http.map((h) => {
                const expanded = open.has(h.id)
                return (
                  <Fragment key={h.id}>
                    <tr>
                      <td className={styles.mono}>{safeText(h.method)}</td>
                      <td className={styles.mono}><span className={styles.cell}>{safeText(h.host)}<CopyButton value={h.host} label="Copy host" /></span></td>
                      <td>
                        <span className={styles.cell}>
                          <button type="button" className={`${styles.rowButton} ${styles.mono}`} aria-expanded={expanded} onClick={() => toggle(h.id)}>
                            {safeText(pathOf(h.url))}
                          </button>
                          <CopyButton value={h.url} label="Copy URL" />
                        </span>
                      </td>
                      <td className={styles.mono}>{h.status ?? '-'}</td>
                    </tr>
                    {expanded && (
                      <tr>
                        <td colSpan={4}>
                          <p className={styles.detailLabel}>Request headers</p>
                          <pre className={styles.detail}>{safeLines(h.reqHeaders)}</pre>
                          <p className={styles.detailLabel}>Response headers</p>
                          <pre className={styles.detail}>{safeLines(h.respHeaders)}</pre>
                          {h.hasBody && (
                            <p>
                              <button type="button" className={styles.linkButton} onClick={() => void download(httpBodyUrl(taskId, h.id), `http-${h.id}.zip`)}>
                                Download body (ZIP)
                              </button>
                            </p>
                          )}
                        </td>
                      </tr>
                    )}
                  </Fragment>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
