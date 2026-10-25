import type { TaskReportDto } from '../../api/types'
import { Tabs } from '../../kit/Tabs'
import { useUrlState, type UrlField } from '../../kit/useUrlState'
import { DetectionsTab } from './DetectionsTab'
import { EventsTab } from './EventsTab'
import { FilesTab } from './FilesTab'
import { HttpTab } from './HttpTab'
import { NetworkTab } from './NetworkTab'
import { OverviewTab } from './OverviewTab'
import { ProcessesTab } from './ProcessesTab'
import { ScreenshotsTab } from './ScreenshotsTab'

export type ReportTab = 'overview' | 'detections' | 'processes' | 'network' | 'http' | 'files' | 'screenshots' | 'events'

const schema = {
  tab: { kind: 'string', default: '' },
  process: { kind: 'number', default: 0, min: 1 },
} satisfies Record<string, UrlField>

/**
 * The in-app report of a completed task. The active tab lives in `?tab=`.
 * `eventCount` is the task's event total (the live hook's backlog); without it the Events tab has no count.
 */
export function TaskReport({ report, taskId, eventCount }: { report: TaskReportDto; taskId: string; eventCount?: number }) {
  const [, set] = useUrlState(schema)
  const show = (tab: ReportTab) => set({ tab })
  const onShowEvents = (processId: number) => set({ tab: 'events', process: processId })

  const tabs: { id: ReportTab; label: string }[] = [
    { id: 'overview', label: 'Overview' },
    { id: 'detections', label: `Detections ${report.detections.length}` },
    { id: 'processes', label: `Processes ${report.processes.length}` },
    { id: 'network', label: `Network ${report.networkFlows.length + report.dns.length}` },
    { id: 'http', label: `HTTP ${report.http.length}` },
    { id: 'files', label: `Files ${report.droppedFiles.length}` },
    { id: 'screenshots', label: `Screenshots ${report.screenshots.length}` },
    { id: 'events', label: eventCount === undefined ? 'Events' : `Events ${eventCount}` },
  ]

  return (
    <Tabs tabs={tabs} label="Report">
      {(active) => {
        switch (active as ReportTab) {
          case 'detections': return <DetectionsTab report={report} />
          case 'processes': return <ProcessesTab report={report} onShowEvents={onShowEvents} />
          case 'network': return <NetworkTab report={report} />
          case 'http': return <HttpTab report={report} taskId={taskId} />
          case 'files': return <FilesTab report={report} />
          case 'screenshots': return <ScreenshotsTab report={report} />
          case 'events': return <EventsTab taskId={taskId} report={report} />
          default: return <OverviewTab report={report} onShowTab={show} />
        }
      }}
    </Tabs>
  )
}
