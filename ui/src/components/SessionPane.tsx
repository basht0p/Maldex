import { useEffect, useState } from 'react'
import { listArtifacts } from '../api/client'
import type { ArtifactDto, RunStatus } from '../api/types'
import styles from './SessionPane.module.css'

/**
 * Plan 1a placeholder for the interactive session. Plan 1b replaces the body of this component with the
 * guacamole-common-js display; the props (task id + live status) stay the same.
 *
 * The newest screenshot is offered as a download: artifact downloads are always password-protected ZIPs
 * (password "infected"), so it cannot be shown inline as an image.
 */
export function SessionPane({ taskId, status }: { taskId: string; status: RunStatus | null | undefined }) {
  const [screenshot, setScreenshot] = useState<ArtifactDto | null>(null)

  useEffect(() => {
    let cancelled = false
    listArtifacts(taskId).then(
      (list) => {
        if (!cancelled) setScreenshot(list.filter((a) => a.kind === 'Screenshot').at(-1) ?? null) // listed oldest first
      },
      () => undefined, // best effort: the placeholder works without it
    )
    return () => {
      cancelled = true
    }
  }, [taskId, status])

  return (
    <section className={styles.pane} aria-label="Session">
      <p className={styles.placeholder}>Live view arrives with real mode</p>
      {screenshot && (
        <p>
          <a href={`/api/artifacts/${screenshot.id}/download`}>Latest screenshot (ZIP, password &quot;infected&quot;)</a>
        </p>
      )}
    </section>
  )
}

export default SessionPane
