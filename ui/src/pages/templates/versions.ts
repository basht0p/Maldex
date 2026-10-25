import type { AdminTemplateDto, TemplateJobDto, TemplateJobState, TemplateVersionDto } from '../../api/types'

/** Edition and build, e.g. `Windows 11 Pro 26300`; empty when neither is known. */
export function windowsLabel(v: TemplateVersionDto): string {
  return [v.windowsEdition, v.windowsBuild].filter((s) => s && s.trim()).join(' ')
}

/** The version a rollback returns to, chosen by the server's role. */
export function rollbackTarget(t: AdminTemplateDto): TemplateVersionDto | undefined {
  return t.versions.find((v) => v.role === 'RollbackTarget')
}

/** Why Roll back is off because the target's disk is missing; undefined otherwise. */
export function rollbackDiskReason(target: TemplateVersionDto | undefined): string | undefined {
  return target !== undefined && !target.diskPresent ? `Cannot roll back: the disk is missing for v${target.number}.` : undefined
}

export function officeLabel(present: boolean | null | undefined): string {
  return present == null ? 'Office unknown' : present ? 'Office ✓' : 'Office ✗'
}

/** Why Edit and Refresh are off for a template that has no current version. */
export const NO_VERSION = 'no version yet'
export const ACTIVE_JOB_STATES: TemplateJobState[] = ['Queued', 'Preparing', 'Installing', 'Provisioning', 'Configuring', 'Sealing', 'Verifying']
/** While a job of the template is active, its jobs are polled at this interval to notice the terminal state. */
export const JOB_POLL_MS = 5000

export const isActiveJob = (j: TemplateJobDto) => ACTIVE_JOB_STATES.includes(j.state)

/** Why Edit and Refresh are off while a job runs. */
export function jobBlockReason(activeJob: TemplateJobDto | undefined): string | undefined {
  return activeJob ? `${/^[AEIOU]/i.test(activeJob.kind) ? 'An' : 'A'} ${activeJob.kind} job is already ${activeJob.state.toLowerCase()} for this template.` : undefined
}

/** Why Edit and Refresh are off when only the id of the active job is known (the list does not carry its kind or state). */
export const JOB_ACTIVE = 'A job is already running for this template.'
export const ALREADY_DEFAULT = 'Already the default.'
