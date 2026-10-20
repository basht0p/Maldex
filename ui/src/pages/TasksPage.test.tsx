import { fireEvent, render, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { MemoryRouter } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import type { TaskDto } from '../api/types'
import TasksPage from './TasksPage'

const task = (over: Partial<TaskDto>): TaskDto => ({
  id: 't1', orgId: 'o1', orgName: 'Acme', sampleSha256: null, url: null, originalFileName: null, image: 'win10',
  networkMode: 'Simulated', exitCountry: null, durationSec: 300, interactive: true, status: 'Queued', verdict: 'Unknown',
  queuePosition: 2, createdAt: '2026-10-19T00:00:00Z', processingWarnings: [], ...over,
})

const server = setupServer(
  http.get('/api/orgs', () => HttpResponse.json([{ id: 'o1', name: 'Acme', retentionDays: 30 }])),
)
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

describe('TasksPage', () => {
  it('renders hostile names as literal text and shows queue position + cancel for queued tasks', async () => {
    server.use(
      http.get('/api/tasks', () =>
        HttpResponse.json({
          items: [task({ originalFileName: 'invoice‮txt.exe' }), task({ id: 't2', status: 'Completed', verdict: 'Malicious', url: '<img src=x onerror=alert(1)>' })],
          total: 2,
        }),
      ),
    )
    const { container } = render(<MemoryRouter><TasksPage /></MemoryRouter>)

    expect(await screen.findByText('invoice\\u202Etxt.exe')).toBeInTheDocument()
    expect(screen.getByText('<img src=x onerror=alert(1)>')).toBeInTheDocument()
    expect(container.querySelector('img')).toBeNull()
    expect(screen.getByText(/#2 in queue/)).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Cancel' })).toHaveLength(1)
    expect(screen.getByText('Malicious')).toBeInTheDocument()
  })

  it('TasksPage_Cancel409_ShowsError', async () => {
    server.use(
      http.get('/api/tasks', () => HttpResponse.json({ items: [task({})], total: 1 })),
      http.post('/api/tasks/t1/cancel', () => HttpResponse.json({ title: 'Conflict' }, { status: 409 })),
    )
    render(<MemoryRouter><TasksPage /></MemoryRouter>)

    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }))

    expect(await screen.findByText('Only queued tasks can be cancelled.')).toBeInTheDocument()
    // The refresh that follows the failed cancel must not wipe the message.
    await new Promise((r) => setTimeout(r, 100))
    expect(screen.getByText('Only queued tasks can be cancelled.')).toBeInTheDocument()
  })
})
