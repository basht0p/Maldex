// Mirrors the REST/SignalR DTOs of Maldex.Web / Maldex.Core. Field names are camelCase and enums travel as strings
// (Program.cs registers a global JsonStringEnumConverter for HTTP and SignalR).

export type RunStatus = 'Queued' | 'Preparing' | 'Running' | 'Collecting' | 'Processing' | 'Completed' | 'Failed' | 'Cancelled'
export type NetworkMode = 'None' | 'Simulated' | 'LiveVpn' | 'LiveTor'
export type Verdict = 'Unknown' | 'NoThreats' | 'Suspicious' | 'Malicious'
export type EventType = 'Process' | 'File' | 'Registry' | 'Network' | 'Dns'
export type ArtifactKind = 'Sample' | 'Dropped' | 'Pcap' | 'Screenshot' | 'Report' | 'MitmFlows'
export type UserRole = 'Analyst' | 'Admin'

export const RUN_STATUSES: readonly RunStatus[] = ['Queued', 'Preparing', 'Running', 'Collecting', 'Processing', 'Completed', 'Failed', 'Cancelled']
export const NETWORK_MODES: readonly NetworkMode[] = ['None', 'Simulated', 'LiveVpn', 'LiveTor']

export interface MeDto {
  id: string
  displayName: string
  role: UserRole
  /** The effective submit cap in bytes; files over it are refused before upload. */
  maxUploadBytes: number
}

export interface OrgDto {
  id: string
  name: string
  retentionDays: number
}

export interface TaskDto {
  id: string
  orgId: string
  orgName: string
  sampleSha256?: string | null
  url?: string | null
  originalFileName?: string | null
  image: string
  networkMode: NetworkMode
  exitCountry?: string | null
  durationSec: number
  interactive: boolean
  status: RunStatus
  verdict: Verdict
  queuePosition: number
  createdAt: string
  startedAt?: string | null
  deadlineAt?: string | null
  completedAt?: string | null
  failureReason?: string | null
  processingWarnings: string[]
  /** The template version the run used; absent for tasks created before templates (show `image` instead). */
  template?: TaskTemplateDto | null
  submittedByName?: string | null
  /** Seconds from start to end of the run; null when it has not both started and ended. */
  ranSec?: number | null
}

export type TemplateVersionOrigin = 'IsoBuild' | 'Edit' | 'Refresh' | 'Imported'
export type TemplateVersionStatus = 'Ready' | 'Retired' | 'Deleted'
/** What a version is for the admin; the server derives it (retention by "was current before"). */
export type TemplateVersionRole = 'Current' | 'Previous' | 'RollbackTarget' | 'Pending' | 'Retired' | 'Deleted'

/** GET /api/templates: the enabled templates an analyst may pick. */
export interface TemplateSummaryDto {
  id: string
  displayName: string
  isDefault: boolean
}

/** What a task shows about the template version it used (TaskDto.template). */
export interface TaskTemplateDto {
  id: string
  displayName: string
  versionNumber: number
  origin: string
  windowsEdition?: string | null
  windowsBuild?: string | null
  officePresent?: boolean | null
  agentVersion?: string | null
  sealedAt: string
  sealedBy?: string | null
  summary: string
}

export interface TemplateVersionDto {
  id: string
  number: number
  origin: TemplateVersionOrigin
  status: TemplateVersionStatus
  role: TemplateVersionRole
  isCurrent: boolean
  diskPresent: boolean
  windowsEdition?: string | null
  windowsBuild?: string | null
  officePresent?: boolean | null
  agentVersion?: string | null
  sealedBy?: string | null
  sealedAt: string
  note?: string | null
  sizeBytes?: number | null
}

export type TemplateJobKind = 'Edit' | 'Refresh' | 'Build'
export type TemplateJobNetwork = 'None' | 'Direct'
export type TemplateJobState = 'Queued' | 'Preparing' | 'Installing' | 'Provisioning' | 'Configuring' | 'Sealing' | 'Verifying' | 'Published' | 'Sealed' | 'Failed' | 'Cancelled'

/** An Edit, Refresh or Build job (GET /api/admin/template-jobs/{id}, the `templateJob` hub event). `idleDeadline` is null outside Configuring. */
export interface TemplateJobDto {
  id: string
  kind: TemplateJobKind
  templateId: string
  /** Null for a Build (it has no base version). */
  baseVersionNumber: number | null
  resultVersionNumber?: number | null
  network: TemplateJobNetwork
  state: TemplateJobState
  message?: string | null
  failureReason?: string | null
  createdAt: string
  endedAt?: string | null
  idleDeadline?: string | null
  /** The installed edition of a Build. */
  edition?: string | null
  hasProvisionLog?: boolean
  /** A Seal was accepted and has not been acted on yet; false again after a refusal (Defender). */
  sealRequested?: boolean
}

export type MediaKind = 'OsInstaller' | 'Application'
export type MediaStatus = 'Receiving' | 'Ready' | 'Failed'

export interface InstallerImageDto {
  index: number
  name: string
  build?: string | null
  architecture?: string | null
  supported: boolean
  unsupportedReason?: string | null
}

/** GET /api/admin/media (Admin only). */
export interface MediaFileDto {
  id: string
  kind: MediaKind
  fileName: string
  size: number
  receivedBytes: number
  sha256?: string | null
  source: 'Upload' | 'Url'
  sourceUrl?: string | null
  status: MediaStatus
  failureReason?: string | null
  images: InstallerImageDto[]
  preferredImageIndex?: number | null
  application?: string | null
  addedBy: string
  addedAt: string
  usedBy: string[]
  /** Template versions that use this media; `usedBy` also names active jobs. */
  usedByRefs?: MediaUsageDto[]
}

export interface MediaUsageDto {
  templateId: string
  templateName: string
  versionNumber: number
  templateExists: boolean
}

export interface StartUploadResponse {
  media: MediaFileDto
  chunkBytes: number
}

/** POST /api/admin/templates/build. `displayName` is needed only when `templateId` is new. */
export interface StartBuildRequest {
  templateId: string
  displayName?: string
  osMediaId: string
  editionIndex?: number
  applicationMediaIds: string[]
  publishOnSuccess: boolean
}

/** GET /api/admin/templates (Admin only). */
export interface AdminTemplateDto {
  id: string
  displayName: string
  description?: string | null
  isDefault: boolean
  enabled: boolean
  current?: TemplateVersionDto | null
  versions: TemplateVersionDto[]
}

export interface TaskListDto {
  items: TaskDto[]
  total: number
}

export interface ProcessNodeDto {
  id: number
  pid: number
  parentId?: number | null
  imagePath: string
  commandLine: string
  user?: string | null
  startedAt: string
  endedAt?: string | null
}

export interface LiveEventDto {
  id: number
  type: EventType
  at: string
  pid: number
  processId?: number | null
  summary: string
  dataJson: string
}

export interface TaskStatusUpdate {
  taskId: string
  status: RunStatus
  verdict: Verdict
  deadlineAt?: string | null
  reason?: string | null
}

export interface ArtifactDto {
  id: string
  kind: ArtifactKind
  sha256: string
  size: number
}

export interface OrgRequest {
  name: string
  retentionDays: number
}

export interface TaskListQuery {
  orgId?: string
  status?: RunStatus
  q?: string
  verdict?: Verdict
  mine?: boolean
  from?: string
  to?: string
  sort?: 'created' | 'verdict' | 'duration'
  dir?: 'asc' | 'desc'
  skip?: number
  take?: number
}

export interface EventsQuery {
  afterId?: number
  type?: EventType
  take?: number
  pid?: number
  processId?: number
  q?: string
}

/** ASP.NET ValidationProblemDetails / ProblemDetails (the parts the UI reads). */
export interface ProblemDetails {
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[]>
}

// --- In-app task report (GET /api/tasks/{id}/report) ---

export type DetectionSource = 'Suricata' | 'Yara' | 'Heuristic'

export interface StatusStepDto {
  from?: RunStatus | null
  to: RunStatus
  at: string
  reason: string
}

export interface DetectionDto {
  source: DetectionSource
  rule: string
  severity: number
  tags: string[]
  target: string
  details?: string | null
}

export interface ReportProcessDto {
  id: number
  parentId?: number | null
  pid: number
  imagePath: string
  commandLine: string
  user?: string | null
  startedAt: string
  endedAt?: string | null
  depth: number
}

export interface NetworkFlowDto {
  proto: string
  src: string
  dst: string
  dstPort: number
  bytes: number
  startedAt: string
}

export interface DnsRowDto {
  at: string
  pid: number
  query: string
  answers: string[]
}

export interface HttpRowDto {
  id: number
  method: string
  url: string
  host: string
  status?: number | null
  reqHeaders: string
  respHeaders: string
  hasBody: boolean
}

export interface DroppedFileDto {
  artifactId: string
  guestPath?: string | null
  sha256: string
  size: number
  createdAt: string
}

export interface ScreenshotRefDto {
  artifactId: string
  at: string
}

export interface SamePreviousLatestDto {
  taskId: string
  verdict: Verdict
  createdAt: string
}

export interface SamePreviousDto {
  count: number
  latest?: SamePreviousLatestDto | null
}

export interface TruncatedDto {
  processes: boolean
  networkFlows: boolean
  dns: boolean
  http: boolean
}

export interface TaskReportDto {
  task: TaskDto
  submittedByName?: string | null
  sha1?: string | null
  md5?: string | null
  sampleSize?: number | null
  fileType?: string | null
  mime?: string | null
  templateSummary?: string | null
  statusHistory: StatusStepDto[]
  detections: DetectionDto[]
  processes: ReportProcessDto[]
  networkFlows: NetworkFlowDto[]
  dns: DnsRowDto[]
  http: HttpRowDto[]
  droppedFiles: DroppedFileDto[]
  screenshots: ScreenshotRefDto[]
  samePrevious: SamePreviousDto
  truncated: TruncatedDto
}

export interface ResubmitRequest {
  orgId?: string | null
  image?: string | null
  networkMode?: string | null
  exitCountry?: string | null
  durationSec?: number | null
  interactive?: boolean | null
}

export interface SlotHolderDto { kind: 'task' | 'job'; id: string; label: string; since: string }
export interface VmSlotStatusDto { capacity: number; busy: number; holders: SlotHolderDto[] }
export interface DiskStatusDto { name: 'data' | 'vm' | 'media'; path: string; freeBytes: number; totalBytes: number }
export interface WorkerStatusDto { name: string; lastTickAt: string | null; expectedIntervalSec: number; stalled: boolean }
export interface RecentFailureDto { kind: 'task' | 'job'; id: string; label: string; reason: string | null; at: string }
export interface SystemStatusDto {
  vmSlot: VmSlotStatusDto
  queueDepth: number
  runningTasks: number
  activeJobs: number
  disks: DiskStatusDto[]
  workers: WorkerStatusDto[]
  recentFailures: RecentFailureDto[]
  generatedAt: string
}
