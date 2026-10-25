import { screen, waitFor, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import userEvent from '@testing-library/user-event'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { TaskDto } from '../api/types'
import { renderWithKit } from '../test/renderWithKit'
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
let confirmSpy: ReturnType<typeof vi.spyOn>
beforeEach(() => { confirmSpy = vi.spyOn(window, 'confirm') })
afterEach(() => {
  server.resetHandlers()
  expect(confirmSpy).not.toHaveBeenCalled() // confirmations are in-app dialogs, never window.confirm
  vi.restoreAllMocks()
})
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
    const { container } = renderWithKit(<TasksPage />)

    expect(await screen.findByText('invoice\\u202Etxt.exe')).toBeInTheDocument()
    expect(screen.getByText('<img src=x onerror=alert(1)>')).toBeInTheDocument()
    expect(container.querySelector('img')).toBeNull()
    expect(screen.getByText(/#2 in queue/)).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Cancel' })).toHaveLength(1)
    expect(screen.getByText('Malicious')).toBeInTheDocument()
  })

  it('asks before cancelling, then toasts "Task cancelled"', async () => {
    let cancelled = 0
    server.use(
      http.get('/api/tasks', () => HttpResponse.json({ items: [task({})], total: 1 })),
      http.post('/api/tasks/t1/cancel', () => { cancelled++; return new HttpResponse(null, { status: 204 }) }),
    )
    renderWithKit(<TasksPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }))
    const dlg = await screen.findByRole('dialog', { name: 'Cancel this queued task?' })
    await userEvent.click(within(dlg).getByRole('button', { name: 'Cancel' }))
    expect(cancelled).toBe(0)

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel task' }))
    expect(await screen.findByText('Task cancelled')).toBeInTheDocument()
    expect(cancelled).toBe(1)
  })

  it('TasksPage_Cancel409_ShowsErrorToast', async () => {
    server.use(
      http.get('/api/tasks', () => HttpResponse.json({ items: [task({})], total: 1 })),
      http.post('/api/tasks/t1/cancel', () => HttpResponse.json({ title: 'Conflict' }, { status: 409 })),
    )
    renderWithKit(<TasksPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel task' }))

    expect(await screen.findByText('Only queued tasks can be cancelled.')).toBeInTheDocument()
    // The refresh that follows the failed cancel must not wipe the toast.
    await new Promise((r) => setTimeout(r, 100))
    expect(screen.getByText('Only queued tasks can be cancelled.')).toBeInTheDocument()
  })

  it('shows Created as a relative time with the exact time in the title', async () => {
    server.use(http.get('/api/tasks', () => HttpResponse.json({ items: [task({})], total: 1 })))
    const { container } = renderWithKit(<TasksPage />)
    await screen.findByText(/#2 in queue/)
    const time = container.querySelector('time')
    expect(time).toHaveAttribute('datetime', '2026-10-19T00:00:00Z')
    expect(time).toHaveAttribute('title', new Date('2026-10-19T00:00:00Z').toLocaleString())
  })

  it('offers copy buttons for a URL and a bare hash', async () => {
    server.use(http.get('/api/tasks', () => HttpResponse.json({ items: [task({ url: 'http://x.test/a' }), task({ id: 't2', sampleSha256: 'f'.repeat(64) })], total: 2 })))
    renderWithKit(<TasksPage />)
    expect(await screen.findByRole('button', { name: 'Copy URL' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy SHA-256' })).toBeInTheDocument()
  })

  it('shows an empty state inviting the first submission', async () => {
    server.use(http.get('/api/tasks', () => HttpResponse.json({ items: [], total: 0 })))
    renderWithKit(<TasksPage />)
    expect(await screen.findByText('Submit your first sample')).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: 'Submit a sample' }).length).toBeGreaterThan(0)
  })

  it('shows a load error with Retry', async () => {
    server.use(http.get('/api/tasks', () => HttpResponse.json({ title: 'Boom' }, { status: 500 })))
    renderWithKit(<TasksPage />)
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    server.use(http.get('/api/tasks', () => HttpResponse.json({ items: [task({})], total: 1 })))
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
    expect(await screen.findByText(/#2 in queue/)).toBeInTheDocument()
  })
})
