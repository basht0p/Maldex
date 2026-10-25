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

export type TemplateJobKind = 'Edit' | 'Refresh'
export type TemplateJobNetwork = 'None' | 'Direct'
export type TemplateJobState = 'Queued' | 'Preparing' | 'Configuring' | 'Sealing' | 'Verifying' | 'Published' | 'Sealed' | 'Failed' | 'Cancelled'

/** An Edit or Refresh job (GET /api/admin/template-jobs/{id}, the `templateJob` hub event). `idleDeadline` is null outside Configuring. */
export interface TemplateJobDto {
  id: string
  kind: TemplateJobKind
  templateId: string
  baseVersionNumber: number
  resultVersionNumber?: number | null
  network: TemplateJobNetwork
  state: TemplateJobState
  message?: string | null
  failureReason?: string | null
  createdAt: string
  endedAt?: string | null
  idleDeadline?: string | null
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
  skip?: number
  take?: number
}

export interface EventsQuery {
  afterId?: number
  type?: EventType
  take?: number
}

/** ASP.NET ValidationProblemDetails / ProblemDetails (the parts the UI reads). */
export interface ProblemDetails {
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[]>
}
