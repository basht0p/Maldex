import { act, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Duration, FileSize, RelativeTime } from './RelativeTime'

afterEach(() => { vi.useRealTimers() })

describe('RelativeTime', () => {
  it('shows the exact local time in title and re-renders after 30 s', () => {
    vi.useFakeTimers()
    vi.setSystemTime(Date.parse('2026-10-25T12:00:00Z'))
    const iso = '2026-10-25T11:59:20Z'
    render(<RelativeTime iso={iso} />)
    const el = screen.getByText('just now')
    expect(el.tagName).toBe('TIME')
    expect(el).toHaveAttribute('title', new Date(iso).toLocaleString())
    act(() => { vi.advanceTimersByTime(30_000) })
    expect(screen.getByText('1 min ago')).toBeInTheDocument()
  })
  it('renders Duration and FileSize', () => {
    render(<><Duration seconds={125} /><FileSize bytes={512} /></>)
    expect(screen.getByText('2:05')).toBeInTheDocument()
    expect(screen.getByText('512 B')).toBeInTheDocument()
  })
})
