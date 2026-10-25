import { afterEach, expect, it, vi } from 'vitest'
import { applyTheme, loadTheme, saveTheme } from './theme'

afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); applyTheme('system') })

it('falls back to system when storage throws or holds junk', () => {
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('denied') })
  expect(loadTheme()).toBe('system')
  vi.restoreAllMocks()
  localStorage.setItem('maldex.theme', 'purple')
  expect(loadTheme()).toBe('system')
})

it('applies and clears data-theme', () => {
  applyTheme('dark')
  expect(document.documentElement.dataset.theme).toBe('dark')
  applyTheme('system')
  expect(document.documentElement.hasAttribute('data-theme')).toBe(false)
})

it('round-trips a saved choice and survives a throwing save', () => {
  saveTheme('light')
  expect(loadTheme()).toBe('light')
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('full') })
  expect(() => saveTheme('dark')).not.toThrow()
})
