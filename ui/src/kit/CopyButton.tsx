import { useEffect, useRef, useState } from 'react'
import styles from './kit.module.css'

function legacyCopy(value: string): boolean {
  const ta = document.createElement('textarea')
  ta.value = value
  ta.setAttribute('readonly', '')
  ta.style.position = 'fixed'
  ta.style.opacity = '0'
  document.body.appendChild(ta)
  ta.select()
  try {
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    document.body.removeChild(ta)
  }
}

export function CopyButton({ value, label = 'Copy' }: { value: string; label?: string }) {
  const [copied, setCopied] = useState(false)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  useEffect(() => () => clearTimeout(timer.current), [])

  async function copy() {
    let ok = false
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(value)
        ok = true
      } else {
        ok = legacyCopy(value)
      }
    } catch {
      ok = legacyCopy(value)
    }
    if (!ok) return
    setCopied(true)
    clearTimeout(timer.current)
    timer.current = setTimeout(() => setCopied(false), 2000)
  }

  return (
    <span className={styles.copyWrap}>
      <button type="button" className={styles.copyButton} aria-label={label} title={label} onClick={() => void copy()}>
        <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true" focusable="false">
          <path fill="currentColor" d="M4 1.5A1.5 1.5 0 0 1 5.5 0h7A1.5 1.5 0 0 1 14 1.5v9a1.5 1.5 0 0 1-1.5 1.5h-7A1.5 1.5 0 0 1 4 10.5zM5.5 1a.5.5 0 0 0-.5.5v9a.5.5 0 0 0 .5.5h7a.5.5 0 0 0 .5-.5v-9a.5.5 0 0 0-.5-.5zM2 4.5v10a.5.5 0 0 0 .5.5h8v1h-8A1.5 1.5 0 0 1 1 14.5v-10z" />
        </svg>
      </button>
      <span role="status" className={styles.copied}>{copied ? 'Copied' : ''}</span>
    </span>
  )
}
