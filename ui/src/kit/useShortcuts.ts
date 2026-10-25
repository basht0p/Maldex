import { useEffect, useRef } from 'react'

const SEQUENCE_MS = 1000

function isTyping(t: EventTarget | null): boolean {
  if (!(t instanceof HTMLElement)) return false
  return t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName)
}

/**
 * Global keyboard shortcuts. Keys are '/', '?', 'Escape' and two-key sequences such as 'g t' (second key within 1 s).
 * Ignored while typing in a field, with Ctrl/Meta/Alt held, or while a modal dialog is open (Escape still works).
 */
export function useShortcuts(map: Record<string, () => void>): void {
  const latest = useRef(map)
  useEffect(() => { latest.current = map })

  useEffect(() => {
    let first: string | null = null
    let timer: ReturnType<typeof setTimeout> | undefined
    const reset = () => { first = null; clearTimeout(timer) }

    const onKeyDown = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey || e.isComposing) return
      const m = latest.current
      if (e.key === 'Escape') { m.Escape?.(); reset(); return }
      if (e.key === 'Shift' || isTyping(e.target)) return
      if (document.querySelector('[aria-modal="true"]')) { reset(); return }

      if (first) {
        const combo = `${first} ${e.key}`
        reset()
        const h = m[combo]
        if (h) { e.preventDefault(); h() }
        return
      }
      if (Object.keys(m).some((k) => k.startsWith(`${e.key} `))) {
        first = e.key
        timer = setTimeout(reset, SEQUENCE_MS)
        return
      }
      const h = m[e.key]
      if (h) { e.preventDefault(); h() }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => { document.removeEventListener('keydown', onKeyDown); reset() }
  }, [])
}
