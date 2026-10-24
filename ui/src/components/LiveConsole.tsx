import { useEffect, useRef, useState } from 'react'
// The package's ESM build exposes a default export only; its typings are a namespace, so cast once here.
import GuacamoleRuntime from 'guacamole-common-js'
import type * as GuacamoleTypes from 'guacamole-common-js'
import styles from './SessionPane.module.css'

const Guacamole = GuacamoleRuntime as typeof GuacamoleTypes

/**
 * The guest display through guacamole-common-js, shared by the task live view and the template job console. While
 * mounted it fetches a single-use token with `getToken`, opens the WebSocket tunnel at `tunnelPath` (same origin) and
 * forwards mouse and keyboard input; on unmount it disconnects. Callers mount it only while a console should exist.
 */
export function LiveConsole({ getToken, tunnelPath }: { getToken: () => Promise<{ token: string }>; tunnelPath: string }) {
  const [error, setError] = useState<string | null>(null)
  const displayRef = useRef<HTMLDivElement>(null)
  const getTokenRef = useRef(getToken)
  useEffect(() => {
    getTokenRef.current = getToken // declared before the connect effect, so that effect sees the latest
  })

  useEffect(() => {
    let cancelled = false
    let client: GuacamoleTypes.Client | undefined
    let keyboard: GuacamoleTypes.Keyboard | undefined

    getTokenRef.current().then(
      ({ token }) => {
        const host = displayRef.current
        if (cancelled || !host) return
        setError(null)
        const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
        const tunnel = new Guacamole.WebSocketTunnel(`${proto}//${window.location.host}${tunnelPath}`)
        const c = new Guacamole.Client(tunnel)
        client = c
        c.onerror = () => {
          if (!cancelled) setError('The live view connection failed.')
        }
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
  }, [tunnelPath])

  return (
    <>
      <div ref={displayRef} className={styles.display} tabIndex={0} aria-label="Live view" />
      {error && (
        <p role="alert" className={styles.error}>
          {error}
        </p>
      )}
    </>
  )
}

export default LiveConsole
