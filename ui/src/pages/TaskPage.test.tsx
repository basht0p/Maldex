import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { TaskDto } from '../api/types'

const live = vi.hoisted(() => ({ value: { status: null, processes: [], events: [], connected: true } as unknown }))
vi.mock('../live/useTaskLive', () => ({ useTaskLive: () => live.value }))
const getTask = vi.fn()
const extendTask = vi.fn()
const stopTask = vi.fn()
const listArtifacts = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  getTask: (...a: unknown[]) => getTask(...a),
  extendTask: (...a: unknown[]) => extendTask(...a),
  stopTask: (...a: unknown[]) => stopTask(...a),
  listArtifacts: (...a: unknown[]) => listArtifacts(...a),
}))

import TaskPage from './TaskPage'

const task = (over: Partial<TaskDto>): TaskDto => ({
  id: 't1', orgId: 'o1', orgName: 'Acme', sampleSha256: null, url: null, originalFileName: 'a.exe', image: 'win10',
  networkMode: 'Simulated', exitCountry: null, durationSec: 300, interactive: true, status: 'Running', verdict: 'Unknown',
  queuePosition: 0, createdAt: '2026-10-19T00:00:00Z', deadlineAt: new Date(Date.now() + 120_000).toISOString(), processingWarnings: [], ...over,
})

const renderPage = () =>
  render(
    <MemoryRouter initialEntries={['/tasks/t1']}>
      <Routes><Route path="/tasks/:id" element={<TaskPage />} /></Routes>
    </MemoryRouter>,
  )

beforeEach(() => {
  vi.clearAllMocks()
  live.value = { status: null, processes: [], events: [], connected: true }
  listArtifacts.mockResolvedValue([])
})

describe('TaskPage', () => {
  it('shows status, a countdown and working Extend/Stop for a running task', async () => {
    getTask.mockResolvedValue(task({}))
    extendTask.mockResolvedValue(task({ deadlineAt: new Date(Date.now() + 180_000).toISOString() }))
    renderPage()
    expect(await screen.findByText('Running')).toBeInTheDocument()
    expect(screen.getByLabelText('Time remaining')).toHaveTextContent(/^[12]:\d\d$/)
    await userEvent.click(screen.getByRole('button', { name: 'Extend +60 s' }))
    expect(extendTask).toHaveBeenCalledWith('t1', 60)
    await userEvent.click(screen.getByRole('button', { name: 'Extend +300 s' }))
    expect(extendTask).toHaveBeenCalledWith('t1', 300)
    stopTask.mockResolvedValue(undefined)
    await userEvent.click(screen.getByRole('button', { name: 'Stop' }))
    expect(stopTask).toHaveBeenCalledWith('t1')
    expect(screen.getByText('Live view arrives with real mode')).toBeInTheDocument()
  })

  it('links the reports and downloads only once Completed', async () => {
    getTask.mockResolvedValue(task({ status: 'Completed', verdict: 'Malicious' }))
    listArtifacts.mockResolvedValue([{ id: 'a1', kind: 'Pcap', sha256: 'abcdef0123', size: 5 }])
    renderPage()
    expect(await screen.findByRole('link', { name: 'HTML report' })).toHaveAttribute('href', '/api/tasks/t1/report.html')
    expect(screen.getByRole('link', { name: 'PDF report' })).toHaveAttribute('href', '/api/tasks/t1/report.pdf')
    expect(screen.getByRole('link', { name: 'Sample (ZIP)' })).toHaveAttribute('href', '/api/tasks/t1/sample')
    expect(await screen.findByRole('link', { name: /Pcap/ })).toHaveAttribute('href', '/api/artifacts/a1/download')
    expect(screen.getByRole('button', { name: 'Stop' })).toBeDisabled()
  })

  it('re-fetches the task after a reconnect so a status missed during the outage is reconciled', async () => {
    const stale = { taskId: 't1', status: 'Running', verdict: 'Unknown', deadlineAt: null }
    live.value = { status: stale, processes: [], events: [], connected: false }
    getTask.mockResolvedValue(task({}))
    const view = renderPage()
    expect(await screen.findByText('Running')).toBeInTheDocument()

    // While disconnected the run completed; the hub reconnects but replays no status.
    getTask.mockResolvedValue(task({ status: 'Completed', verdict: 'Malicious' }))
    live.value = { status: stale, processes: [], events: [], connected: true }
    view.rerender(
      <MemoryRouter initialEntries={['/tasks/t1']}>
        <Routes><Route path="/tasks/:id" element={<TaskPage />} /></Routes>
      </MemoryRouter>,
    )
    expect(await screen.findByText('Completed')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('link', { name: 'HTML report' })).toBeInTheDocument())
    expect(screen.queryByLabelText('Time remaining')).toBeNull()

    // A push newer than the fetch wins again.
    live.value = { status: { ...stale, status: 'Failed', reason: 'x' }, processes: [], events: [], connected: true }
    view.rerender(
      <MemoryRouter initialEntries={['/tasks/t1']}>
        <Routes><Route path="/tasks/:id" element={<TaskPage />} /></Routes>
      </MemoryRouter>,
    )
    expect(await screen.findByText('Failed')).toBeInTheDocument()
  })

  it('does not show report links while running', async () => {
    getTask.mockResolvedValue(task({}))
    renderPage()
    await screen.findByText('Running')
    expect(screen.queryByRole('link', { name: 'HTML report' })).toBeNull()
  })
})
