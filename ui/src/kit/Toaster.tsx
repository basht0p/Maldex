import { useCallback, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import styles from './Toaster.module.css'
import { ToastContext as Ctx, type ToastApi } from './useToast'

type Kind = 'success' | 'error'
type Toast = { id: number; kind: Kind; text: string }

const SUCCESS_MS = 5_000

/** Toast stack, fixed bottom-right. Success toasts vanish after 5 s; errors stay until dismissed. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const nextId = useRef(1)
  const dismiss = useCallback((id: number) => setToasts((t) => t.filter((x) => x.id !== id)), [])
  const push = useCallback((kind: Kind, text: string) => {
    const id = nextId.current++
    setToasts((t) => [...t, { id, kind, text }])
    if (kind === 'success') setTimeout(() => dismiss(id), SUCCESS_MS)
  }, [dismiss])
  const api = useMemo<ToastApi>(() => ({ success: (t) => push('success', t), error: (t) => push('error', t) }), [push])

  return (
    <Ctx.Provider value={api}>
      {children}
      <div className={styles.stack} role="status" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} className={`${styles.toast} ${styles[t.kind]}`}>
            <span className={styles.text}>{t.text}</span>
            {t.kind === 'error' && <button type="button" className={styles.dismiss} onClick={() => dismiss(t.id)}>Dismiss</button>}
          </div>
        ))}
      </div>
    </Ctx.Provider>
  )
}
