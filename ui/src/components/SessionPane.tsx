import { useEffect, useState } from 'react'
import { getTunnelToken, listArtifacts } from '../api/client'
import type { ArtifactDto, RunStatus } from '../api/types'
import LiveConsole from './LiveConsole'
import styles from './SessionPane.module.css'

/**
 * The interactive session. While the task is Running it shows the guest display through guacamole-common-js: a
 * single-use token is fetched, the WebSocket tunnel proxies to guacd, and mouse/keyboard input is forwarded.
 * In every other status it shows a placeholder.
 *
 * The newest screenshot is offered as a download: artifact downloads are always password-protected ZIPs
 * (password "infected"), so it cannot be shown inline as an image.
 */
export function SessionPane({ taskId, status }: { taskId: string; status: RunStatus | null | undefined }) {
  const [screenshot, setScreenshot] = useState<ArtifactDto | null>(null)
  const running = status === 'Running'

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
      {running ? (
        <LiveConsole getToken={() => getTunnelToken(taskId)} tunnelPath={`/api/tasks/${encodeURIComponent(taskId)}/tunnel`} />
      ) : (
        <p className={styles.placeholder}>The live view is available while the task is running.</p>
      )}
      {screenshot && (
        <p>
          <a href={`/api/artifacts/${screenshot.id}/download`}>Latest screenshot (ZIP, password &quot;infected&quot;)</a>
        </p>
      )}
    </section>
  )
}

export default SessionPane
