import { ApiError } from '../api/client'
import type { ProblemDetails } from '../api/types'

/** The messages to show for a failed request: validation errors, else the problem title (a 409 carries the reason there). */
export function messageOf(e: unknown): string[] {
  if (e instanceof ApiError) {
    const p = (e.problem ?? {}) as ProblemDetails
    if (p.errors) return Object.values(p.errors).flat()
    return [p.title ?? e.message]
  }
  return [e instanceof Error ? e.message : 'Request failed.']
}
