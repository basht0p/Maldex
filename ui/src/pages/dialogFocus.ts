import { useEffect } from 'react'
import type { KeyboardEvent, RefObject } from 'react'

export function focusables(root: HTMLElement | null): HTMLElement[] {
  if (!root) return []
  return Array.from(root.querySelectorAll<HTMLElement>('input, button, select, textarea, a[href], [tabindex]:not([tabindex="-1"])')).filter((el) => !el.hasAttribute('disabled'))
}

/**
 * Focus handling shared by the inline dialogs: focus moves into the dialog on open; the returned handler traps Tab and
 * closes on Escape (unless busy). The caller restores focus to its trigger when it closes.
 */
export function useDialogFocus(ref: RefObject<HTMLDivElement | null>, busy: boolean, onClose: () => void) {
  useEffect(() => {
    focusables(ref.current)[0]?.focus()
  }, [ref])

  return function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') {
      e.stopPropagation()
      if (!busy) onClose()
      return
    }
    if (e.key !== 'Tab') return
    const items = focusables(ref.current)
    if (items.length === 0) return
    const first = items[0]
    const last = items[items.length - 1]
    const inside = ref.current?.contains(document.activeElement) ?? false
    if (e.shiftKey && (document.activeElement === first || !inside)) { e.preventDefault(); last.focus() }
    else if (!e.shiftKey && (document.activeElement === last || !inside)) { e.preventDefault(); first.focus() }
  }
}
