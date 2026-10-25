import { useState, type ReactNode } from 'react'
import { FileSize } from '../../kit/RelativeTime'
import { safeText } from '../../util/safeText'
import styles from './DropZone.module.css'

export type DropItem = { id: number; file: File; status?: ReactNode; extra?: ReactNode }

type Props = {
  items: DropItem[]
  disabled?: boolean
  onAdd(files: File[]): void
  onRemove(id: number): void
  describedBy?: string
}

/** Accepts dropped or picked files (several at once) and lists each with its size, a status slot and a remove button. */
export function DropZone({ items, disabled, onAdd, onRemove, describedBy }: Props) {
  const [over, setOver] = useState(false)
  return (
    <div>
      <div
        className={`${styles.zone} ${over ? styles.over : ''}`}
        onDragOver={(e) => {
          if (disabled) return
          e.preventDefault()
          setOver(true)
        }}
        onDragLeave={() => setOver(false)}
        onDrop={(e) => {
          e.preventDefault()
          setOver(false)
          if (!disabled) onAdd(Array.from(e.dataTransfer?.files ?? []))
        }}
      >
        <span>Drop samples here, or choose them</span>
        <input
          id="file"
          type="file"
          multiple
          disabled={disabled}
          aria-describedby={describedBy}
          onChange={(e) => {
            onAdd(Array.from(e.target.files ?? []))
            e.target.value = ''
          }}
        />
      </div>
      {items.length > 0 && (
        <ul className={styles.list}>
          {items.map((it) => (
            <li key={it.id} className={styles.row}>
              <div className={styles.line}>
                <span className={styles.name}>{safeText(it.file.name)}</span>
                <span className={styles.size}><FileSize bytes={it.file.size} /></span>
                {it.status && <span className={styles.status}>{it.status}</span>}
                <button type="button" className={styles.remove} aria-label={`Remove ${safeText(it.file.name)}`} disabled={disabled} onClick={() => onRemove(it.id)}>
                  Remove
                </button>
              </div>
              {it.extra && <div className={styles.extra}>{it.extra}</div>}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
