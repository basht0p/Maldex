import type { NetworkMode } from '../../api/types'

export type LastOptions = {
  orgId: string
  image: string
  networkMode: NetworkMode
  exitCountry: string
  durationSec: string
  interactive: boolean
}

const KEY = 'maldex.submit.lastOptions'
const STRING_FIELDS = ['orgId', 'image', 'networkMode', 'exitCountry', 'durationSec'] as const

/** Reads the remembered submit options; any storage or parse failure yields `{}`. Unknown or mistyped fields are dropped. */
export function loadLastOptions(): Partial<LastOptions> {
  try {
    const raw = localStorage.getItem(KEY)
    if (!raw) return {}
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {}
    const src = parsed as Record<string, unknown>
    const out: Record<string, unknown> = {}
    for (const f of STRING_FIELDS) {
      if (typeof src[f] === 'string') out[f] = src[f]
    }
    if (typeof src.interactive === 'boolean') out.interactive = src.interactive
    return out as Partial<LastOptions>
  } catch {
    return {}
  }
}

export function saveLastOptions(o: LastOptions): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(o))
  } catch {
    // storage unavailable: remembering is a convenience only
  }
}
