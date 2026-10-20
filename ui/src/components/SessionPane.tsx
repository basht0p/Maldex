import { useEffect, useRef, useState } from 'react'
// The package's ESM build exposes a default export only; its typings are a namespace, so cast once here.
import GuacamoleRuntime from 'guacamole-common-js'
import type * as GuacamoleTypes from 'guacamole-common-js'
import { getTunnelToken, listArtifacts } from '../api/client'
import type { ArtifactDto, RunStatus } from '../api/types'
import styles from './SessionPane.module.css'

const Guacamole = GuacamoleRuntime as typeof GuacamoleTypes

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
  const [error, setError] = useState<string | null>(null)
  const displayRef = useRef<HTMLDivElement>(null)
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

  useEffect(() => {
    if (!running) return
    let cancelled = false
    let client: GuacamoleTypes.Client | undefined
    let keyboard: GuacamoleTypes.Keyboard | undefined

    getTunnelToken(taskId).then(
      ({ token }) => {
        setError(null)
        const host = displayRef.current
        if (cancelled || !host) return
        const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
        const tunnel = new Guacamole.WebSocketTunnel(`${proto}//${window.location.host}/api/tasks/${encodeURIComponent(taskId)}/tunnel`)
        const c = new Guacamole.Client(tunnel)
        client = c
        c.onerror = () => setError('The live view connection failed.')
        const display = c.getDisplay()
        const element = display.getElement()
        host.replaceChildren(element)
        display.onresize = (w: number) => {
          if (w > 0 && host.clientWidth > 0) display.scale(Math.min(1, host.clientWidth / w))
        }

        const mouse = new Guacamole.Mouse(element)
        const send = (e: GuacamoleTypes.Mouse.Event) => c.sendMouseState(e.state, true)
        mouse.onEach(['mousedown', 'mouseup', 'mousemove'], send as never)

        // The keyboard listens on the (focusable) display only, so typing elsewhere on the page is unaffected.
        keyboard = new Guacamole.Keyboard(host)
        keyboard.onkeydown = (keysym: number) => c.sendKeyEvent(1, keysym)
        keyboard.onkeyup = (keysym: number) => c.sendKeyEvent(0, keysym)

        c.connect(`token=${encodeURIComponent(token)}&width=1280&height=800`)
      },
      () => {
        if (!cancelled) setError('Could not start the live view.')
      },
    )

    const host = displayRef.current
    return () => {
      cancelled = true
      if (keyboard) {
        keyboard.onkeydown = null
        keyboard.onkeyup = null
        keyboard.reset()
      }
      client?.disconnect()
      host?.replaceChildren()
    }
  }, [taskId, running])

  return (
    <section className={styles.pane} aria-label="Session">
      {running ? (
        <div ref={displayRef} className={styles.display} tabIndex={0} aria-label="Live view" />
      ) : (
        <p className={styles.placeholder}>The live view is available while the task is running.</p>
      )}
      {error && (
        <p role="alert" className={styles.error}>
          {error}
        </p>
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
