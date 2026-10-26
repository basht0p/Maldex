import { notifyBlocked, notifyNoRole, notifyUnauthorized } from '../auth/session'
import type {
  AdminTemplateDto,
  TemplateDetailDto,
  ArtifactDto,
  AuthConfigDto,
  AuthNextDto,
  RecoveryCodesDto,
  TotpConfirmDto,
  TotpSetupDto,
  EventsQuery,
  LiveEventDto,
  MediaFileDto,
  MediaKind,
  MeDto,
  OrgDto,
  OrgRequest,
  ProcessNodeDto,
  ResubmitRequest,
  StartBuildRequest,
  StartUploadResponse,
  TaskDto,
  TaskListDto,
  TaskListQuery,
  SystemStatusDto,
  AuthSettingsDto,
  CreateUserRequest,
  UpdateUserRequest,
  UserDto,
  TaskReportDto,
  TemplateJobDto,
  TemplateJobNetwork,
  TemplateSummaryDto,
} from './types'

/** A non-2xx response. `problem` is the parsed body (ProblemDetails for JSON, the raw text otherwise, or undefined when empty). */
export class ApiError extends Error {
  readonly status: number
  readonly problem: unknown
  /** The machine-readable `error` of the problem body (for example "invalid" or "no_role"), when there is one. */
  readonly code: string | undefined

  constructor(status: number, problem: unknown) {
    super(problemTitle(problem) ?? `Request failed with status ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
    this.code = problemCode(problem)
  }
}

function problemTitle(problem: unknown): string | undefined {
  if (problem && typeof problem === 'object' && 'title' in problem && typeof problem.title === 'string') return problem.title
  return undefined
}

function problemCode(problem: unknown): string | undefined {
  if (problem && typeof problem === 'object' && 'error' in problem && typeof problem.error === 'string') return problem.error
  return undefined
}

/** Session-level reactions to a failed response. Sign-in calls (/api/auth/*) answer 401 for a wrong password, which is not an expired session. */
function react(path: string, err: ApiError): void {
  if (err.status === 401 && !path.startsWith('/api/auth/')) notifyUnauthorized()
  else if (err.status === 403 && err.code === 'no_role') notifyNoRole()
  else if (err.status === 403 && (err.code === 'csrf' || err.code === 'origin')) notifyBlocked()
}

async function parseBody(res: Response): Promise<unknown> {
  const text = await res.text()
  if (!text) return undefined
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

async function request<T>(method: string, path: string, body?: BodyInit | null, json?: unknown, extra?: { headers?: Record<string, string>; signal?: AbortSignal }): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...extra?.headers }
  if (method !== 'GET') headers['X-Maldex-Csrf'] = '1'
  let payload = body ?? undefined
  if (json !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(json)
  }
  // FormData bodies must not get a Content-Type: the browser adds the multipart boundary itself.
  const res = await fetch(path, { method, headers, body: payload, signal: extra?.signal, credentials: 'same-origin' })
  const parsed = await parseBody(res)
  if (!res.ok) {
    const err = new ApiError(res.status, parsed)
    react(path, err)
    throw err
  }
  return parsed as T
}

/** GET `path` as a Blob (same-origin cookie). Throws {@link ApiError} on a non-2xx response. */
export async function fetchBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  const res = await fetch(path, { method: 'GET', headers: { Accept: '*/*' }, signal, credentials: 'same-origin' })
  if (!res.ok) {
    const err = new ApiError(res.status, await parseBody(res))
    react(path, err)
    throw err
  }
  return res.blob()
}

/** Downloads `path` (see {@link fetchBlob}) and saves it as `fileName`. */
export async function downloadFile(path: string, fileName: string): Promise<void> {
  const blob = await fetchBlob(path)
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  a.style.display = 'none'
  document.body.appendChild(a)
  try {
    a.click()
  } finally {
    a.remove()
    // Revoked on the next task: the click has started the download by then.
    setTimeout(() => URL.revokeObjectURL(url), 0)
  }
}

function query(params:Record<string, string | number | undefined>): string {
  const q = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== '') q.set(k, String(v))
  const s = q.toString()
  return s ? `?${s}` : ''
}

const get = <T>(path: string) => request<T>('GET', path)
const post = <T>(path: string, json?: unknown) => request<T>('POST', path, undefined, json)

export const getMe = () => get<MeDto>('/api/me')

export const getAuthConfig = () => get<AuthConfigDto>('/api/auth/config')
export const login = (userName: string, password: string) => post<AuthNextDto>('/api/auth/login', { userName, password })
export const loginTotp = (code: string) => post<AuthNextDto>('/api/auth/login/totp', { code })
export const changePassword = (currentPassword: string, newPassword: string) => post<AuthNextDto>('/api/auth/password', { currentPassword, newPassword })
export const totpSetup = () => post<TotpSetupDto>('/api/auth/totp/setup')
export const totpConfirm = (code: string) => post<TotpConfirmDto>('/api/auth/totp/confirm', { code })
export const removeTotp = (password: string) => request<void>('DELETE', '/api/auth/totp', undefined, { password })
export const regenerateRecoveryCodes = (password: string) => post<RecoveryCodesDto>('/api/auth/totp/recovery-codes', { password })
export const logout = () => post<void>('/api/auth/logout')
/** SSO start is a full-page navigation, not a fetch. */
export const ssoStartUrl = (returnUrl: string) => `/api/auth/sso/start?returnUrl=${encodeURIComponent(returnUrl)}`

export const listOrgs = () => get<OrgDto[]>('/api/orgs')
export const createOrg = (req: OrgRequest) => request<OrgDto>('POST', '/api/orgs', undefined, req)
export const updateOrg = (id: string, req: OrgRequest) => request<OrgDto>('PUT', `/api/orgs/${encodeURIComponent(id)}`, undefined, req)

/** `form` is multipart: orgId, one of file/url, and optionally networkMode, durationSec, interactive, exitCountry, image. */
export const submitTask = (form: FormData) => request<TaskDto>('POST', '/api/tasks', form)
export const listTasks = (q: TaskListQuery = {}) =>
  get<TaskListDto>(`/api/tasks${query({ orgId: q.orgId, status: q.status, q: q.q, verdict: q.verdict, mine: q.mine ? 'true' : undefined, from: q.from, to: q.to, sort: q.sort, dir: q.dir, skip: q.skip, take: q.take })}`)
export const getTask = (id: string) => get<TaskDto>(`/api/tasks/${encodeURIComponent(id)}`)
export const cancelTask = (id: string) => post<void>(`/api/tasks/${encodeURIComponent(id)}/cancel`)
export const extendTask = (id: string, seconds: number) => post<TaskDto>(`/api/tasks/${encodeURIComponent(id)}/extend`, { seconds })
export const stopTask = (id: string) => post<void>(`/api/tasks/${encodeURIComponent(id)}/stop`)

export const getProcesses = (id: string) => get<ProcessNodeDto[]>(`/api/tasks/${encodeURIComponent(id)}/processes`)
export const getEvents = (id: string, q: EventsQuery = {}) =>
  get<LiveEventDto[]>(`/api/tasks/${encodeURIComponent(id)}/events${query({ afterId: q.afterId, type: q.type, take: q.take, pid: q.pid, processId: q.processId, q: q.q })}`)
export const eventsExportUrl = (id: string, format: 'csv' | 'json', q: Omit<EventsQuery, 'afterId' | 'take'> = {}) =>
  `/api/tasks/${encodeURIComponent(id)}/events/export${query({ format, type: q.type, pid: q.pid, processId: q.processId, q: q.q })}`
export const listArtifacts = (id: string) => get<ArtifactDto[]>(`/api/tasks/${encodeURIComponent(id)}/artifacts`)
export const getTunnelToken = (id: string) => get<{ token: string }>(`/api/tasks/${encodeURIComponent(id)}/tunnel-token`)

export const listTemplates = () => get<TemplateSummaryDto[]>('/api/templates')
export const listAdminTemplates = () => get<AdminTemplateDto[]>('/api/admin/templates')
export const getAdminTemplate = (id: string) => get<TemplateDetailDto>(`/api/admin/templates/${encodeURIComponent(id)}`)
export const setDefaultTemplate = (id: string) => post<void>(`/api/admin/templates/${encodeURIComponent(id)}/default`)
export const setTemplateEnabled = (id: string, enabled: boolean) => post<void>(`/api/admin/templates/${encodeURIComponent(id)}/enabled`, { enabled })
/** Makes the kept previous version current again; returns the updated template. */
export const rollBackTemplate = (id: string) => post<AdminTemplateDto>(`/api/admin/templates/${encodeURIComponent(id)}/rollback`)

const jobPath = (jobId: string) => `/api/admin/template-jobs/${encodeURIComponent(jobId)}`
/** Starts an Edit job (202); 409 carries the reason (a job is active, the base disk is missing, low disk space). */
export const startTemplateEdit = (id: string, network: TemplateJobNetwork, publishOnSuccess: boolean) =>
  post<TemplateJobDto>(`/api/admin/templates/${encodeURIComponent(id)}/edit`, { network, publishOnSuccess })
export const startTemplateRefresh = (id: string, publishOnSuccess: boolean) =>
  post<TemplateJobDto>(`/api/admin/templates/${encodeURIComponent(id)}/refresh`, { publishOnSuccess })
export const listTemplateJobs = (id: string) => get<TemplateJobDto[]>(`/api/admin/templates/${encodeURIComponent(id)}/jobs`)
export const getTemplateJob = (jobId: string) => get<TemplateJobDto>(jobPath(jobId))
export const sealTemplateJob = (jobId: string, note?: string) => post<void>(`${jobPath(jobId)}/seal`, { note: note || null })
export const cancelTemplateJob = (jobId: string) => post<void>(`${jobPath(jobId)}/cancel`)
export const getTemplateJobTunnelToken = (jobId: string) => get<{ token: string }>(`${jobPath(jobId)}/tunnel-token`)
/** Publishes a held (Pending) version; returns the updated template. */
export const publishTemplateVersion = (id: string, versionId: string) =>
  post<AdminTemplateDto>(`/api/admin/templates/${encodeURIComponent(id)}/versions/${encodeURIComponent(versionId)}/publish`)
export const discardTemplateVersion = (id: string, versionId: string) =>
  post<void>(`/api/admin/templates/${encodeURIComponent(id)}/versions/${encodeURIComponent(versionId)}/discard`)

/** Starts a Build job (202); 400/409 carry the reason in the title. */
export const startTemplateBuild = (req: StartBuildRequest) => post<TemplateJobDto>('/api/admin/templates/build', req)
/** Only for a template with no versions; 409 carries the reason. */
export const deleteTemplate = (id: string) => request<void>('DELETE', `/api/admin/templates/${encodeURIComponent(id)}`)
export const provisionLogUrl = (jobId: string) => `${jobPath(jobId)}/provision-log`

export const listMedia = () => get<MediaFileDto[]>('/api/admin/media')
export const getMedia = (id: string) => get<MediaFileDto>(`/api/admin/media/${encodeURIComponent(id)}`)
export const startMediaUpload = (fileName: string, size: number, kind: MediaKind) =>
  post<StartUploadResponse>('/api/admin/media/uploads', { fileName, size, kind })
/** One chunk at `offset`. A 409 carries the server's `receivedBytes` in its problem body. */
export const putMediaChunk = (id: string, offset: number, chunk: Blob, signal?: AbortSignal) =>
  request<{ receivedBytes: number; status: string }>('PUT', `/api/admin/media/uploads/${encodeURIComponent(id)}`, chunk, undefined, { headers: { 'Upload-Offset': String(offset) }, signal })
export const fetchMedia = (url: string, kind: MediaKind, fileName?: string) =>
  post<MediaFileDto>('/api/admin/media/fetch', { url, kind, fileName: fileName || null })
export const deleteMedia = (id: string) => request<void>('DELETE', `/api/admin/media/${encodeURIComponent(id)}`)

export const getTaskReport = (id: string) => get<TaskReportDto>(`/api/tasks/${encodeURIComponent(id)}/report`)
export const screenshotUrl = (artifactId: string) => `/api/artifacts/${encodeURIComponent(artifactId)}/image`
export const httpBodyUrl = (taskId: string, txId: number) => `/api/tasks/${encodeURIComponent(taskId)}/http/${txId}/body`
export const resubmitTask = (id: string, req: ResubmitRequest) => post<TaskDto>(`/api/tasks/${encodeURIComponent(id)}/resubmit`, req)

export const getSystemStatus = (signal?: AbortSignal) => request<SystemStatusDto>('GET', '/api/admin/status', undefined, undefined, { signal })

const userPath = (id: string) => `/api/admin/users/${encodeURIComponent(id)}`
export const listUsers = () => get<UserDto[]>('/api/admin/users')
export const createUser = (req: CreateUserRequest) => post<UserDto>('/api/admin/users', req)
export const updateUser = (id: string, req: UpdateUserRequest) => request<UserDto>('PATCH', userPath(id), undefined, req)
export const resetUserPassword = (id: string, newPassword: string) => post<void>(`${userPath(id)}/reset-password`, { newPassword })
export const clearUserTotp = (id: string) => post<void>(`${userPath(id)}/clear-totp`)
export const getAuthSettings = () => get<AuthSettingsDto>('/api/admin/settings')
export const putAuthSettings = (req: AuthSettingsDto) => request<AuthSettingsDto>('PUT', '/api/admin/settings', undefined, req)
