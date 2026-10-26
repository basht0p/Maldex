import { useRef } from 'react'
import { useDialogFocus } from '../pages/dialogFocus'
import styles from './Confirm.module.css'
import kit from './kit.module.css'

const SHORTCUTS: ReadonlyArray<readonly [string, string]> = [
  ['/', 'Focus the search box'],
  ['?', 'Show this help'],
  ['g t', 'Go to Tasks'],
  ['g s', 'Go to Submit'],
  ['g a', 'Go to Administration (admins)'],
  ['g u', 'Go to Users (admins)'],
  ['Esc', 'Close a dialog or menu'],
]

export function ShortcutHelp({ onClose }: { onClose: () => void }) {
  const ref = useRef<HTMLDivElement>(null)
  const onKeyDown = useDialogFocus(ref, false, onClose)
  return (
    <div className={styles.backdrop}>
      <div ref={ref} role="dialog" aria-modal="true" aria-labelledby="shortcuts-title" className={styles.dialog} onKeyDown={onKeyDown}>
        <h3 id="shortcuts-title">Keyboard shortcuts</h3>
        <ul className={kit.shortcutList}>
          {SHORTCUTS.map(([keys, what]) => (
            <li key={keys}><span>{keys.split(' ').map((k) => <kbd key={k}>{k}</kbd>)}</span> {what}</li>
          ))}
        </ul>
        <div className={styles.actions}>
          <button type="button" className={styles.cancel} onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  )
}
