import { describe, expect, it } from 'vitest'
import { formatDuration, formatFileSize, formatRelative } from './format'

const now = Date.parse('2026-10-25T12:00:00Z')

describe('format', () => {
  it('formats relative times', () => {
    expect(formatRelative('2026-10-25T11:59:30Z', now)).toBe('just now')
    expect(formatRelative('2026-10-25T11:57:00Z', now)).toBe('3 min ago')
    expect(formatRelative('2026-10-25T09:00:00Z', now)).toBe('3 h ago')
    expect(formatRelative('2026-10-22T12:00:00Z', now)).toBe('3 d ago')
    expect(formatRelative('2026-10-25T12:05:00Z', now)).toBe('in 5 min')
    expect(formatRelative('2026-10-25T15:00:00Z', now)).toBe('in 3 h')
    expect(formatRelative('2026-10-01T12:00:00Z', now)).toBe(new Date('2026-10-01T12:00:00Z').toLocaleDateString())
    expect(formatRelative('2026-11-20T12:00:00Z', now)).toBe(new Date('2026-11-20T12:00:00Z').toLocaleDateString())
  })
  it('formats durations', () => {
    expect(formatDuration(0)).toBe('0:00')
    expect(formatDuration(125)).toBe('2:05')
    expect(formatDuration(3600)).toBe('1:00:00')
    expect(formatDuration(3725)).toBe('1:02:05')
  })
  it('formats sizes in binary units', () => {
    expect(formatFileSize(512)).toBe('512 B')
    expect(formatFileSize(49357)).toBe('48.2 KiB')
    expect(formatFileSize(5.4 * 1024 ** 3)).toBe('5.4 GiB')
  })
})
