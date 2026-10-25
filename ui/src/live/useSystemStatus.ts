import { createContext, useContext } from 'react'
import type { SystemStatusDto } from '../api/types'

export const LOW_DISK_FRACTION = 0.1

export interface SystemStatusValue {
  status: SystemStatusDto | null
  error: string | null
  refresh(): void
  attention: boolean
}

export const SystemStatusContext = createContext<SystemStatusValue>({ status: null, error: null, refresh: () => undefined, attention: false })

export function needsAttention(s: SystemStatusDto | null): boolean {
  if (!s) return false
  return s.workers.some((w) => w.stalled) || s.disks.some((d) => d.totalBytes > 0 && d.freeBytes / d.totalBytes < LOW_DISK_FRACTION)
}

export const useSystemStatus = () => useContext(SystemStatusContext)
