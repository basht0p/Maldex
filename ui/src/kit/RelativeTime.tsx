import { useEffect, useState } from 'react'
import { formatDuration, formatFileSize, formatRelative } from './format'

export function RelativeTime({ iso }: { iso: string }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 30_000)
    return () => clearInterval(id)
  }, [])
  const d = new Date(iso)
  const title = Number.isNaN(d.getTime()) ? undefined : d.toLocaleString()
  return <time dateTime={iso} title={title}>{formatRelative(iso, now)}</time>
}

export function Duration({ seconds }: { seconds: number }) {
  return <span>{formatDuration(seconds)}</span>
}

export function FileSize({ bytes }: { bytes: number }) {
  return <span>{formatFileSize(bytes)}</span>
}
