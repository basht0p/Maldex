import type { ReactNode } from 'react'
import markDark from '../../../brand/logo/maldex-mark.svg'
import markLight from '../../../brand/logo/maldex-mark-light.svg'
import app from '../App.module.css'
import { ThemeSwitch } from '../kit/ThemeSwitch'
import styles from './AuthLayout.module.css'

/** The page chrome for /login, /account and /waiting: brand mark and a card, without the main nav. */
export function AuthLayout({ title, wide, children }: { title: string; wide?: boolean; children: ReactNode }) {
  return (
    <div className={styles.page}>
      <div className={styles.themeSwitch}><ThemeSwitch /></div>
      <span className={app.brand}>
        <img className={`${app.mark} ${app.markDark}`} src={markDark} alt="" />
        <img className={`${app.mark} ${app.markLight}`} src={markLight} alt="" />
        <span className={app.wordmark}>MAL<span>DEX</span></span>
      </span>
      <main className={`${styles.card} ${wide ? styles.wide : ''}`}>
        <h1>{title}</h1>
        {children}
      </main>
    </div>
  )
}
