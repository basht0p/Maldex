const MIN = 60
const HOUR = 3600
const DAY = 86400

/** Relative time such as "3 min ago" or "in 5 min". Whole units, rounded down. 7 days or more gives a date. */
export function formatRelative(iso: string, now: number): string {
  const t = Date.parse(iso)
  if (Number.isNaN(t)) return ''
  const diff = (t - now) / 1000
  const abs = Math.abs(diff)
  if (abs < 45) return 'just now'
  if (abs >= 7 * DAY) return new Date(t).toLocaleDateString()
  let text: string
  if (abs < HOUR) text = `${Math.max(1, Math.floor(abs / MIN))} min`
  else if (abs < DAY) text = `${Math.floor(abs / HOUR)} h`
  else text = `${Math.floor(abs / DAY)} d`
  return diff < 0 ? `${text} ago` : `in ${text}`
}

/** Seconds as m:ss, or h:mm:ss from one hour up. */
export function formatDuration(seconds: number): string {
  const total = Math.max(0, Math.floor(Number.isFinite(seconds) ? seconds : 0))
  const h = Math.floor(total / HOUR)
  const m = Math.floor((total % HOUR) / MIN)
  const s = total % MIN
  const ss = String(s).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`
}

/** Bytes in binary units: B, KiB, MiB, GiB, TiB, with one decimal above bytes. */
export function formatFileSize(bytes: number): string {
  const b = Math.max(0, Number.isFinite(bytes) ? bytes : 0)
  if (b < 1024) return `${Math.round(b)} B`
  const units = ['KiB', 'MiB', 'GiB', 'TiB']
  let value = b
  let i = -1
  do {
    value /= 1024
    i++
  } while (value >= 1024 && i < units.length - 1)
  return `${value.toFixed(1)} ${units[i]}`
}
