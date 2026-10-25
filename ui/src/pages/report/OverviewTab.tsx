import { useId, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import type { TaskReportDto } from '../../api/types'
import { CopyButton } from '../../kit/CopyButton'
import { Duration, FileSize } from '../../kit/RelativeTime'
import { safeText } from '../../util/safeText'
import { formatTime, verdictLabel } from './reportUtil'
import { ProcessingWarnings, Screenshot } from './shared'
import type { ReportTab } from './TaskReport'
import styles from './report.module.css'

const TOP = 5
const STRIP = 4

function Card({ title, wide, children }: { title: string; wide?: boolean; children: ReactNode }) {
  const id = useId()
  return (
    <section className={wide ? `${styles.card} ${styles.wide}` : styles.card} aria-labelledby={id}>
      <h2 id={id}>{title}</h2>
      {children}
    </section>
  )
}

function WhyCard({ report, onShowTab }: { report: TaskReportDto; onShowTab: (tab: ReportTab) => void }) {
  const all = report.detections
  // Stable: equal severities keep the server order.
  const top = all.map((d, i) => ({ d, i })).sort((a, b) => b.d.severity - a.d.severity || a.i - b.i).slice(0, TOP)
  return (
    <Card title={`Why ${verdictLabel(report.task.verdict)}`}>
      {all.length === 0 ? (
        <p className={styles.muted}>No detections</p>
      ) : (
        <>
          <ul className={styles.list}>
            {top.map(({ d, i }) => (
              <li key={i}>
                <span className={styles.sev} aria-label={`Severity ${d.severity}`}>{d.severity}</span>
                <span className={styles.mono}>{safeText(d.rule)}</span>
              </li>
            ))}
          </ul>
          <p>
            <button type="button" className={styles.linkButton} onClick={() => onShowTab('detections')}>
              {`All ${all.length} detections`}
            </button>
          </p>
        </>
      )}
    </Card>
  )
}

function SampleCard({ report }: { report: TaskReportDto }) {
  const { task } = report
  if (!task.sampleSha256 && task.url) {
    return (
      <Card title="URL">
        <p className={styles.facts}>
          <span className={styles.mono}>{safeText(task.url)}</span>
          <CopyButton value={task.url} label="Copy URL" />
        </p>
      </Card>
    )
  }
  const hashes: [string, string | null | undefined][] = [['SHA-256', task.sampleSha256], ['SHA-1', report.sha1], ['MD5', report.md5]]
  const others = report.samePrevious.count
  return (
    <Card title="Sample">
      {task.originalFileName && <p className={styles.mono}>{safeText(task.originalFileName)}</p>}
      <dl className={styles.hashes}>
        {hashes.filter(([, v]) => v).map(([name, value]) => (
          <div key={name} className={styles.hashRow}>
            <dt>{name}</dt>
            <dd>
              <span>{value}</span>
              <CopyButton value={value!} label={`Copy ${name}`} />
            </dd>
          </div>
        ))}
      </dl>
      <p className={styles.facts}>
        {report.sampleSize != null && <FileSize bytes={report.sampleSize} />}
        {report.fileType && <span>{safeText(report.fileType)}</span>}
        {others > 0 && task.sampleSha256 && (
          <Link to={`/?q=${encodeURIComponent(task.sampleSha256)}`}>
            {others === 1 ? '1 other task with this hash' : `${others} other tasks with this hash`}
          </Link>
        )}
      </p>
    </Card>
  )
}

function ContactedCard({ report }: { report: TaskReportDto }) {
  // DNS queries first, then flow destinations, then HTTP URLs; distinct, at most five in all.
  const seen = new Set<string>()
  const values: string[] = []
  for (const v of [
    ...report.dns.map((d) => d.query),
    ...report.networkFlows.map((f) => `${f.dst}:${f.dstPort}`),
    ...report.http.map((h) => h.url),
  ]) {
    if (values.length >= TOP) break
    if (!v || seen.has(v)) continue
    seen.add(v)
    values.push(v)
  }
  return (
    <Card title="Contacted">
      {values.length === 0 ? (
        <p className={styles.muted}>No network contacts</p>
      ) : (
        <ul className={styles.list}>
          {values.map((v) => (
            <li key={v}>
              <span className={styles.mono}>{safeText(v)}</span>
              <CopyButton value={v} label={`Copy ${safeText(v)}`} />
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}

function ScreenshotStrip({ report, onShowTab }: { report: TaskReportDto; onShowTab: (tab: ReportTab) => void }) {
  const shots = report.screenshots
  const extra = shots.length - STRIP
  return (
    <Card title="Screenshots" wide>
      {shots.length === 0 ? (
        <p className={styles.muted}>No screenshots</p>
      ) : (
        <div className={styles.strip}>
          {shots.slice(0, STRIP).map((s) => <Screenshot key={s.artifactId} artifactId={s.artifactId} at={s.at} />)}
          {extra > 0 && (
            <button type="button" className={`${styles.linkButton} ${styles.more}`} onClick={() => onShowTab('screenshots')}>
              {`+${extra}`}
            </button>
          )}
        </div>
      )}
    </Card>
  )
}

function StatusTimeline({ report }: { report: TaskReportDto }) {
  const steps = report.statusHistory
  return (
    <Card title="Status timeline" wide>
      {steps.length === 0 ? (
        <p className={styles.muted}>No status history</p>
      ) : (
        <ol className={styles.timeline}>
          {steps.map((s, i) => {
            const next = steps[i + 1]
            const spent = next ? (Date.parse(next.at) - Date.parse(s.at)) / 1000 : null
            return (
              <li key={i}>
                <strong>{s.to}</strong>
                <span className={styles.muted}>
                  <time dateTime={s.at}>{formatTime(s.at)}</time>
                  {s.reason && <> · {safeText(s.reason)}</>}
                </span>
                <span className={styles.mono}>{spent !== null && Number.isFinite(spent) ? <Duration seconds={spent} /> : null}</span>
              </li>
            )
          })}
        </ol>
      )}
    </Card>
  )
}

export function OverviewTab({ report, onShowTab }: { report: TaskReportDto; onShowTab: (tab: ReportTab) => void }) {
  return (
    <div className={styles.overview}>
      <ProcessingWarnings warnings={report.task.processingWarnings} />
      <WhyCard report={report} onShowTab={onShowTab} />
      <SampleCard report={report} />
      <ContactedCard report={report} />
      <ScreenshotStrip report={report} onShowTab={onShowTab} />
      <StatusTimeline report={report} />
    </div>
  )
}
