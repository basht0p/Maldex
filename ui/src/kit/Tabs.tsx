import { useId, useRef, type KeyboardEvent, type ReactNode } from 'react'
import { useUrlState, type UrlField } from './useUrlState'
import styles from './Tabs.module.css'

const schema = { tab: { kind: 'string', default: '' } } satisfies Record<string, UrlField>

export function Tabs(props: {
  tabs: { id: string; label: ReactNode }[]
  children: (active: string) => ReactNode
  label: string
}) {
  const { tabs, children, label } = props
  const [{ tab }, set] = useUrlState(schema)
  const base = useId()
  const refs = useRef<(HTMLButtonElement | null)[]>([])
  const found = tabs.findIndex((t) => t.id === tab)
  const activeIndex = found >= 0 ? found : 0
  const active = tabs[activeIndex]?.id ?? ''

  const select = (i: number) => {
    set({ tab: tabs[i].id })
    refs.current[i]?.focus()
  }

  const onKeyDown = (e: KeyboardEvent) => {
    const n = tabs.length
    if (n === 0) return
    let i: number
    switch (e.key) {
      case 'ArrowRight': i = (activeIndex + 1) % n; break
      case 'ArrowLeft': i = (activeIndex - 1 + n) % n; break
      case 'Home': i = 0; break
      case 'End': i = n - 1; break
      default: return
    }
    e.preventDefault()
    select(i)
  }

  return (
    <div>
      <div role="tablist" aria-label={label} className={styles.list} onKeyDown={onKeyDown}>
        {tabs.map((t, i) => (
          <button
            key={t.id}
            ref={(el) => { refs.current[i] = el }}
            type="button"
            role="tab"
            id={`${base}-tab-${t.id}`}
            aria-selected={i === activeIndex}
            aria-controls={`${base}-panel`}
            tabIndex={i === activeIndex ? 0 : -1}
            className={styles.tab}
            onClick={() => select(i)}
          >
            {t.label}
          </button>
        ))}
      </div>
      <div role="tabpanel" id={`${base}-panel`} aria-labelledby={`${base}-tab-${active}`} className={styles.panel}>
        {children(active)}
      </div>
    </div>
  )
}
