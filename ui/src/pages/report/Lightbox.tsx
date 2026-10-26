import { useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { useDialogFocus } from '../dialogFocus'
import forms from '../forms.module.css'
import { AuthedImage } from './shared'
import styles from './report.module.css'

/** `src` is a same-origin API path, loaded with the session cookie. */
export interface LightboxItem { src: string; caption: string }

/** Modal image viewer. ArrowLeft/ArrowRight wrap, Escape closes. Pass `onIndex` to control the index from the parent. */
export function Lightbox({ items, index, onClose, onIndex }: {
  items: LightboxItem[]
  index: number
  onClose: () => void
  onIndex?: (i: number) => void
}) {
  const ref = useRef<HTMLDivElement>(null)
  const [own, setOwn] = useState(index)
  const current = onIndex ? index : own
  const go = (i: number) => {
    const next = (i + items.length) % items.length
    if (onIndex) onIndex(next)
    else setOwn(next)
  }
  const trap = useDialogFocus(ref, false, onClose)
  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'ArrowLeft') { e.preventDefault(); go(current - 1) }
    else if (e.key === 'ArrowRight') { e.preventDefault(); go(current + 1) }
    else trap(e)
  }
  const item = items[current]
  if (!item) return null
  return (
    <div className={styles.lightboxBackdrop} onClick={onClose}>
      <div
        ref={ref}
        role="dialog"
        aria-modal="true"
        aria-label={`Screenshot ${current + 1} of ${items.length}`}
        className={styles.lightbox}
        onKeyDown={onKeyDown}
        onClick={(e) => e.stopPropagation()}
      >
        <AuthedImage key={item.src} path={item.src} alt={item.caption} className={styles.lightboxImg} />
        <p className={styles.muted}>{item.caption}</p>
        <div className={styles.lightboxBar}>
          <button type="button" className={forms.secondary} onClick={() => go(current - 1)}>Previous</button>
          <button type="button" className={forms.secondary} onClick={() => go(current + 1)}>Next</button>
          <button type="button" className={forms.secondary} onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  )
}
