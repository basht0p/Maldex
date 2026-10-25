import { useState } from 'react'
import { applyTheme, loadTheme, saveTheme } from './theme'
import type { ThemeChoice } from './theme'
import styles from './kit.module.css'

export function ThemeSwitch() {
  const [choice, setChoice] = useState<ThemeChoice>(loadTheme)
  return (
    <select
      className={styles.themeSelect}
      aria-label="Theme"
      value={choice}
      onChange={(e) => {
        const c = e.target.value as ThemeChoice
        setChoice(c)
        applyTheme(c)
        saveTheme(c)
      }}
    >
      <option value="system">System</option>
      <option value="light">Light</option>
      <option value="dark">Dark</option>
    </select>
  )
}
