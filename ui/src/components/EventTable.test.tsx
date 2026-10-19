import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { EventType, LiveEventDto } from '../api/types'
import { EventTable } from './EventTable'

const ev = (id: number, type: EventType, summary: string): LiveEventDto => ({
  id, type, at: '2026-10-19T00:00:00Z', pid: 100, processId: 1, summary, dataJson: '{}',
})

const events = [
  ev(1, 'Process', 'start cmd.exe'),
  ev(2, 'File', 'write C:\\temp\\a.dll'),
  ev(3, 'Registry', 'set HKCU\\Run\\evil'),
  ev(4, 'Network', 'connect 10.0.0.1:443'),
  ev(5, 'Dns', 'query evil.example.com'),
]

describe('EventTable', () => {
  it('EventTable_FiltersByType', async () => {
    render(<EventTable events={events} />)
    expect(screen.getAllByRole('row')).toHaveLength(1 + 5) // header + 5
    await userEvent.click(screen.getByRole('button', { name: 'File' }))
    expect(screen.getByText('write C:\\temp\\a.dll')).toBeInTheDocument()
    expect(screen.queryByText('start cmd.exe')).toBeNull()
    expect(screen.getAllByRole('row')).toHaveLength(1 + 1)
    await userEvent.click(screen.getByRole('button', { name: 'Dns' })) // chips toggle; two selected
    expect(screen.getAllByRole('row')).toHaveLength(1 + 2)
    await userEvent.click(screen.getByRole('button', { name: 'File' }))
    await userEvent.click(screen.getByRole('button', { name: 'Dns' })) // none selected = all
    expect(screen.getAllByRole('row')).toHaveLength(1 + 5)
  })

  it('EventTable_SearchesSummary', async () => {
    render(<EventTable events={events} />)
    await userEvent.type(screen.getByRole('searchbox', { name: /search/i }), 'EVIL')
    expect(screen.getByText('set HKCU\\Run\\evil')).toBeInTheDocument()
    expect(screen.getByText('query evil.example.com')).toBeInTheDocument()
    expect(screen.queryByText('start cmd.exe')).toBeNull()
  })

  it('accepts an initial type filter and renders hostile summaries as text', () => {
    const hostile = '<img src=x onerror=alert(1)>'
    const { container } = render(<EventTable events={[ev(1, 'File', hostile), ev(2, 'Dns', 'x')]} filter={{ types: ['File'] }} />)
    expect(screen.getByText(hostile)).toBeInTheDocument()
    expect(container.querySelector('img')).toBeNull()
    expect(screen.getAllByRole('row')).toHaveLength(2)
  })

  it('virtualizes once past 500 rows', () => {
    const many = Array.from({ length: 5000 }, (_, i) => ev(i + 1, 'File', `write file${i}`))
    render(<EventTable events={many} />)
    expect(screen.getAllByRole('row').length).toBeLessThan(200)
    expect(screen.getByText(/5000 events/)).toBeInTheDocument()
  })
})
