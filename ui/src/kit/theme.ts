export type ThemeChoice = 'system' | 'light' | 'dark'

const KEY = 'maldex.theme'

/** The saved choice; storage that is blocked, throws or holds junk reads as 'system'. */
export function loadTheme(): ThemeChoice {
  try {
    const v = localStorage.getItem(KEY)
    return v === 'light' || v === 'dark' ? v : 'system'
  } catch {
    return 'system'
  }
}

/** 'system' removes data-theme so the tokens follow prefers-color-scheme. */
export function applyTheme(c: ThemeChoice): void {
  const root = document.documentElement
  if (c === 'system') root.removeAttribute('data-theme')
  else root.dataset.theme = c
}

export function saveTheme(c: ThemeChoice): void {
  try {
    if (c === 'system') localStorage.removeItem(KEY)
    else localStorage.setItem(KEY, c)
  } catch {
    /* storage unavailable: the choice lasts for this page only */
  }
}
