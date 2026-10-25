import type { DnsRowDto, NetworkFlowDto, TaskReportDto } from '../../api/types'
import { CopyButton } from '../../kit/CopyButton'
import { EmptyState } from '../../kit/EmptyState'
import { FileSize } from '../../kit/RelativeTime'
import { SortHeader } from '../../kit/SortHeader'
import { safeText } from '../../util/safeText'
import forms from '../forms.module.css'
import { formatTime, useSorted } from './reportUtil'
import { TruncatedNotice } from './shared'
import styles from './report.module.css'

const FLOW_KEYS = {
  time: (f: NetworkFlowDto) => f.startedAt,
  proto: (f: NetworkFlowDto) => f.proto,
  src: (f: NetworkFlowDto) => f.src,
  dst: (f: NetworkFlowDto) => f.dst,
  port: (f: NetworkFlowDto) => f.dstPort,
  bytes: (f: NetworkFlowDto) => f.bytes,
}

const DNS_KEYS = {
  time: (d: DnsRowDto) => d.at,
  pid: (d: DnsRowDto) => d.pid,
  query: (d: DnsRowDto) => d.query.toLowerCase(),
}

function Flows({ rows, truncated }: { rows: NetworkFlowDto[]; truncated: boolean }) {
  const { sorted, sort, dir, onSort } = useSorted(rows, FLOW_KEYS, { sort: 'time', dir: 'asc' })
  const h = (field: string, label: string) => <SortHeader field={field} label={label} sort={sort} dir={dir} onSort={onSort} />
  return (
    <section className={styles.section} aria-label="Flows">
      <h2>Flows</h2>
      <TruncatedNotice show={truncated} />
      {rows.length === 0 ? (
        <EmptyState title="No flows" />
      ) : (
        <div className={styles.scroll}>
          <table className={forms.table}>
            <thead>
              <tr>{h('time', 'Time')}{h('proto', 'Proto')}{h('src', 'Source')}{h('dst', 'Destination')}{h('port', 'Port')}{h('bytes', 'Bytes')}</tr>
            </thead>
            <tbody>
              {sorted.map((f, i) => (
                <tr key={i}>
                  <td><time dateTime={f.startedAt}>{formatTime(f.startedAt)}</time></td>
                  <td>{safeText(f.proto)}</td>
                  <td className={styles.mono}><span className={styles.cell}>{safeText(f.src)}<CopyButton value={f.src} label="Copy source" /></span></td>
                  <td className={styles.mono}><span className={styles.cell}>{safeText(f.dst)}<CopyButton value={f.dst} label="Copy destination" /></span></td>
                  <td className={styles.mono}>{f.dstPort}</td>
                  <td><FileSize bytes={f.bytes} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

function Dns({ rows, truncated }: { rows: DnsRowDto[]; truncated: boolean }) {
  const { sorted, sort, dir, onSort } = useSorted(rows, DNS_KEYS, { sort: 'time', dir: 'asc' })
  const h = (field: string, label: string) => <SortHeader field={field} label={label} sort={sort} dir={dir} onSort={onSort} />
  return (
    <section className={styles.section} aria-label="DNS">
      <h2>DNS</h2>
      <TruncatedNotice show={truncated} />
      {rows.length === 0 ? (
        <EmptyState title="No DNS queries" />
      ) : (
        <div className={styles.scroll}>
          <table className={forms.table}>
            <thead>
              <tr>{h('time', 'Time')}{h('pid', 'PID')}{h('query', 'Query')}<th scope="col">Answers</th></tr>
            </thead>
            <tbody>
              {sorted.map((d, i) => (
                <tr key={i}>
                  <td><time dateTime={d.at}>{formatTime(d.at)}</time></td>
                  <td className={styles.mono}>{d.pid}</td>
                  <td className={styles.mono}><span className={styles.cell}>{safeText(d.query)}<CopyButton value={d.query} label="Copy query" /></span></td>
                  <td className={styles.mono}>
                    {d.answers.length > 0 && (
                      <span className={styles.cell}>{d.answers.map(safeText).join(', ')}<CopyButton value={d.answers.join('\n')} label="Copy answers" /></span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

export function NetworkTab({ report }: { report: TaskReportDto }) {
  return (
    <div>
      <Flows rows={report.networkFlows} truncated={report.truncated.networkFlows} />
      <Dns rows={report.dns} truncated={report.truncated.dns} />
    </div>
  )
}
