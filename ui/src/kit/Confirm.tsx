import { useCallback, useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useDialogFocus } from '../pages/dialogFocus'
import styles from './Confirm.module.css'

import { ConfirmContext as Ctx, type Confirm, type ConfirmOptions } from './useConfirm'

type Pending = ConfirmOptions & { resolve: (ok: boolean) => void }

function ConfirmDialog({ pending, onDone }: { pending: Pending; onDone: (ok: boolean) => void }) {
  const ref = useRef<HTMLDivElement>(null)
  const onKeyDown = useDialogFocus(ref, false, () => onDone(false))
  return (
    <div className={styles.backdrop}>
      <div ref={ref} role="dialog" aria-modal="true" aria-labelledby="confirm-title" className={styles.dialog} onKeyDown={onKeyDown}>
        <h3 id="confirm-title">{pending.title}</h3>
        {pending.body && <p className={styles.body}>{pending.body}</p>}
        <div className={styles.actions}>
          {/* Cancel comes first so it is the default focus. */}
          <button type="button" className={styles.cancel} onClick={() => onDone(false)}>Cancel</button>
          <button type="button" className={`${styles.confirm} ${pending.danger ? styles.danger : ''}`} onClick={() => onDone(true)}>{pending.confirmLabel}</button>
        </div>
      </div>
    </div>
  )
}

/** Hosts one modal confirmation at a time; focus returns to the element that opened it. */
export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [pending, setPending] = useState<Pending | null>(null)
  const opener = useRef<HTMLElement | null>(null)
  const current = useRef<Pending | null>(null)

  const confirm = useCallback<Confirm>((o) => new Promise<boolean>((resolve) => {
    current.current?.resolve(false) // a newer request supersedes the open one
    opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const p = { ...o, resolve }
    current.current = p
    setPending(p)
  }), [])

  const done = useCallback((ok: boolean) => {
    current.current?.resolve(ok)
    current.current = null
    setPending(null)
    const el = opener.current
    opener.current = null
    setTimeout(() => el?.focus(), 0)
  }, [])

  useEffect(() => () => current.current?.resolve(false), [])

  return (
    <Ctx.Provider value={confirm}>
      {children}
      {pending && <ConfirmDialog key={pending.title + pending.confirmLabel} pending={pending} onDone={done} />}
    </Ctx.Provider>
  )
}
