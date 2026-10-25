import { useRef, useState } from 'react'
import { screenshotUrl } from '../../api/client'
import type { TaskReportDto } from '../../api/types'
import { EmptyState } from '../../kit/EmptyState'
import { Lightbox } from './Lightbox'
import { formatTime } from './reportUtil'
import { Screenshot } from './shared'
import styles from './report.module.css'

export function ScreenshotsTab({ report }: { report: TaskReportDto }) {
  const [open, setOpen] = useState<number | null>(null)
  const opener = useRef<HTMLButtonElement | null>(null)
  const shots = report.screenshots
  if (shots.length === 0) return <EmptyState title="No screenshots" />
  const items = shots.map((s) => ({ src: screenshotUrl(s.artifactId), caption: `Screenshot at ${formatTime(s.at)}` }))

  function close() {
    setOpen(null)
    // Restore focus once the dialog has unmounted.
    queueMicrotask(() => opener.current?.focus())
  }

  return (
    <>
      <div className={styles.gallery}>
        {shots.map((s, i) => (
          <figure key={s.artifactId}>
            <button
              type="button"
              className={styles.thumb}
              aria-label={`Open screenshot ${i + 1} of ${shots.length}`}
              onClick={(e) => { opener.current = e.currentTarget; setOpen(i) }}
            >
              <Screenshot artifactId={s.artifactId} at={s.at} />
            </button>
            <figcaption><time dateTime={s.at}>{formatTime(s.at)}</time></figcaption>
          </figure>
        ))}
      </div>
      {open !== null && <Lightbox items={items} index={open} onIndex={setOpen} onClose={close} />}
    </>
  )
}
