import { useCallback, useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'

export type UrlField =
  | { kind: 'string'; default: string }
  | { kind: 'number'; default: number; min?: number }
  | { kind: 'boolean'; default: boolean }
  | { kind: 'date'; default: string } // yyyy-mm-dd or ''
  | { kind: 'enum'; values: readonly string[]; default: string }

type Values<S extends Record<string, UrlField>> = { [K in keyof S]: S[K]['default'] }

function validDate(v: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(v)) return false
  const d = new Date(v + 'T00:00:00Z')
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === v
}

function parse(field: UrlField, raw: string | null): string | number | boolean {
  if (raw === null) return field.default
  switch (field.kind) {
    case 'string':
      return raw
    case 'number': {
      if (raw.trim() === '') return field.default
      const n = Number(raw)
      if (!Number.isFinite(n) || (field.min !== undefined && n < field.min)) return field.default
      return n
    }
    case 'boolean':
      return raw === 'true' || raw === '1' ? true : raw === 'false' || raw === '0' ? false : field.default
    case 'date':
      return validDate(raw) ? raw : field.default
    case 'enum':
      return field.values.includes(raw) ? raw : field.default
  }
}

/** Typed, validated view of URL query keys. `set` merges into the current query (other keys survive) and replaces history. */
export function useUrlState<S extends Record<string, UrlField>>(
  schema: S,
): [values: Values<S>, set: (patch: Partial<Values<S>>) => void] {
  const [params, setParams] = useSearchParams()

  const values = useMemo(() => {
    const out: Record<string, string | number | boolean> = {}
    for (const key of Object.keys(schema)) out[key] = parse(schema[key], params.get(key))
    return out as Values<S>
  }, [params, schema])

  const set = useCallback(
    (patch: Partial<Values<S>>) => {
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          for (const key of Object.keys(patch)) {
            const field = schema[key]
            if (!field) continue
            const value = (patch as Record<string, string | number | boolean | undefined>)[key]
            const badDate = field.kind === 'date' && typeof value === 'string' && value !== '' && !validDate(value)
            if (value === undefined || value === field.default || badDate) next.delete(key)
            else next.set(key, String(value))
          }
          return next
        },
        { replace: true },
      )
    },
    [schema, setParams],
  )

  return [values, set]
}
