import type { TaskDto, TaskReportDto } from '../api/types'

export const HOSTILE = '<script>alert(1)</script>'
export const SHA256 = 'a'.repeat(64)

export function taskFixture(over: Partial<TaskDto> = {}): TaskDto {
  return {
    id: 't1', orgId: 'o1', orgName: 'Acme', sampleSha256: SHA256, url: null, originalFileName: 'dropper.exe', image: 'win10',
    networkMode: 'Simulated', exitCountry: null, durationSec: 300, interactive: true, status: 'Completed', verdict: 'Malicious',
    queuePosition: 0, createdAt: '2026-10-19T00:00:00Z', startedAt: '2026-10-19T00:00:10Z', completedAt: '2026-10-19T00:05:10Z',
    processingWarnings: [], submittedByName: 'Bob Analyst', ranSec: 240, ...over,
  }
}

/** A completed task's report, with hostile strings in commandLine, details and reqHeaders. */
export function reportFixture(over: Partial<TaskReportDto> = {}): TaskReportDto {
  return {
    task: taskFixture(),
    submittedByName: 'Bob Analyst',
    sha1: 'b'.repeat(40),
    md5: 'c'.repeat(32),
    sampleSize: 2048,
    fileType: 'PE32 executable',
    mime: 'application/x-dosexec',
    templateSummary: 'win11-golden v3',
    statusHistory: [
      { from: null, to: 'Queued', at: '2026-10-19T00:00:00Z', reason: 'Submitted' },
      { from: 'Queued', to: 'Running', at: '2026-10-19T00:00:10Z', reason: 'Started' },
      { from: 'Running', to: 'Completed', at: '2026-10-19T00:05:10Z', reason: 'Done' },
    ],
    detections: [
      { source: 'Yara', rule: 'Rule_Sev3', severity: 3, tags: [], target: 'dropper.exe', details: HOSTILE },
      { source: 'Suricata', rule: 'Rule_Sev9', severity: 9, tags: [], target: '10.0.0.5', details: 'sig 1' },
      { source: 'Heuristic', rule: 'Rule_Sev5', severity: 5, tags: [], target: 'x', details: null },
      { source: 'Yara', rule: 'Rule_Sev7', severity: 7, tags: [], target: 'x', details: null },
      { source: 'Yara', rule: 'Rule_Sev1', severity: 1, tags: [], target: 'x', details: null },
      { source: 'Heuristic', rule: 'Rule_Sev8', severity: 8, tags: [], target: 'x', details: null },
      { source: 'Suricata', rule: 'Rule_Sev2', severity: 2, tags: [], target: 'x', details: null },
    ],
    processes: [
      { id: 1, parentId: null, pid: 100, imagePath: 'C:\\dropper.exe', commandLine: `dropper.exe ${HOSTILE}`, user: 'u', startedAt: '2026-10-19T00:00:11Z', endedAt: null, depth: 0 },
    ],
    networkFlows: [
      { proto: 'TCP', src: '10.0.0.2', dst: '93.184.216.34', dstPort: 443, bytes: 1200, startedAt: '2026-10-19T00:00:20Z' },
      { proto: 'UDP', src: '10.0.0.2', dst: '8.8.8.8', dstPort: 53, bytes: 80, startedAt: '2026-10-19T00:00:15Z' },
    ],
    dns: [{ at: '2026-10-19T00:00:14Z', pid: 100, query: 'evil.example', answers: ['93.184.216.34'] }],
    http: [
      { id: 42, method: 'GET', url: 'http://evil.example/payload.bin?x=1', host: 'evil.example', status: 200, reqHeaders: `User-Agent: ${HOSTILE}`, respHeaders: 'Content-Type: application/octet-stream', hasBody: true },
    ],
    droppedFiles: [
      { artifactId: 'f1', guestPath: 'C:\\Users\\a\\payload.dll', sha256: 'd'.repeat(64), size: 4096, createdAt: '2026-10-19T00:01:00Z' },
    ],
    screenshots: Array.from({ length: 6 }, (_, i) => ({ artifactId: `s${i}`, at: `2026-10-19T00:0${i}:00Z` })),
    samePrevious: { count: 2, latest: { taskId: 't0', verdict: 'Malicious', createdAt: '2026-10-18T00:00:00Z' } },
    truncated: { processes: false, networkFlows: false, dns: false, http: false },
    ...over,
  }
}
