import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import type { TaskDto } from '../api/types'
import { reportFixture } from '../test/reportFixture'

const live = vi.hoisted(() => ({ value: { status: null, processes: [], events: [], connected: true } as unknown }))
vi.mock('../live/useTaskLive', () => ({ useTaskLive: () => live.value }))
const getTask = vi.fn()
const extendTask = vi.fn()
const stopTask = vi.fn()
const listArtifacts = vi.fn()
const getTaskReport = vi.fn()
vi.mock('../auth/auth', () => ({ getAccessToken: vi.fn(async () => null) }))
import { getAccessToken } from '../auth/auth'
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  getTask: (...a: unknown[]) => getTask(...a),
  extendTask: (...a: unknown[]) => extendTask(...a),
  stopTask: (...a: unknown[]) => stopTask(...a),
  listArtifacts: (...a: unknown[]) => listArtifacts(...a),
  getTaskReport: (...a: unknown[]) => getTaskReport(...a),
  getEvents: () => Promise.resolve([]),
}))

import { renderWithKit } from '../test/renderWithKit'
import TaskPage from './TaskPage'

const task = (over: Partial<TaskDto>): TaskDto => ({
  id: 't1', orgId: 'o1', orgName: 'Acme', sampleSha256: null, url: null, originalFileName: 'a.exe', image: 'win10',
  networkMode: 'Simulated', exitCountry: null, durationSec: 300, interactive: true, status: 'Running', verdict: 'Unknown',
  queuePosition: 0, createdAt: '2026-10-19T00:00:00Z', deadlineAt: new Date(Date.now() + 120_000).toISOString(), processingWarnings: [], ...over,
})

const routes = () => <Routes><Route path="/tasks/:id" element={<TaskPage />} /></Routes>
const renderPage = () => renderWithKit(routes(), { route: '/tasks/t1' })

let confirmSpy: ReturnType<typeof vi.spyOn>
beforeEach(() => {
  vi.clearAllMocks()
  confirmSpy = vi.spyOn(window, 'confirm')
  live.value = { status: null, processes: [], events: [], connected: true }
  listArtifacts.mockResolvedValue([])
  getTaskReport.mockResolvedValue(reportFixture())
})
afterEach(() => {
  expect(confirmSpy).not.toHaveBeenCalled() // confirmations are in-app dialogs, never window.confirm
  vi.restoreAllMocks()
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
    expect(await screen.findByText('Extended by 60 s')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Extend +300 s' }))
    expect(extendTask).toHaveBeenCalledWith('t1', 300)
    stopTask.mockResolvedValue(undefined)
    await userEvent.click(screen.getByRole('button', { name: 'Stop' }))
    const dlg = await screen.findByRole('dialog', { name: 'Stop this task?' })
    await userEvent.click(within(dlg).getByRole('button', { name: 'Cancel' }))
    expect(stopTask).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Stop' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Stop task' }))
    expect(stopTask).toHaveBeenCalledWith('t1')
    expect(await screen.findByText('Task stopped')).toBeInTheDocument()
    expect(screen.getByLabelText('Live view')).toBeInTheDocument()
  })

  it('shows an action failure as an error toast', async () => {
    getTask.mockResolvedValue(task({}))
    extendTask.mockRejectedValue(new Error('Too late to extend.'))
    renderPage()
    await screen.findByText('Running')
    await userEvent.click(screen.getByRole('button', { name: 'Extend +60 s' }))
    expect(await screen.findByText('Too late to extend.')).toBeInTheDocument()
  })

  it('offers a Copy SHA-256 button beside the sample hash', async () => {
    getTask.mockResolvedValue(task({ sampleSha256: 'a'.repeat(64) }))
    renderPage()
    await screen.findByText('a'.repeat(64))
    expect(screen.getByRole('button', { name: 'Copy SHA-256' })).toBeInTheDocument()
  })

  it('shows a load error with Retry', async () => {
    getTask.mockRejectedValueOnce(new Error('Boom'))
    renderPage()
    expect(await screen.findByRole('alert')).toHaveTextContent('Boom')
    getTask.mockResolvedValue(task({}))
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByText('Running')).toBeInTheDocument()
  })

  it('links the reports and downloads from the Reports menu once Completed', async () => {
    getTask.mockResolvedValue(task({ status: 'Completed', verdict: 'Malicious' }))
    listArtifacts.mockResolvedValue([{ id: 'a1', kind: 'Pcap', sha256: 'abcdef0123', size: 5 }])
    renderPage()
    const menu = await screen.findByRole('button', { name: 'Reports' })
    expect(menu).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('link', { name: 'HTML report' })).toBeNull()
    await userEvent.click(menu)
    expect(menu).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('link', { name: 'HTML report' })).toHaveAttribute('href', '/api/tasks/t1/report.html')
    expect(screen.getByRole('button', { name: 'PDF report' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sample (ZIP)' })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: /Pcap/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Stop' })).toBeDisabled()
  })

  it('downloads the PDF, sample and artifacts with the bearer token under their file names', async () => {
    getTask.mockResolvedValue(task({ status: 'Completed', verdict: 'Malicious', sampleSha256: 'f'.repeat(64) }))
    listArtifacts.mockResolvedValue([{ id: 'a1', kind: 'Pcap', sha256: 'abcdef0123', size: 5 }])
    vi.mocked(getAccessToken).mockResolvedValue('tok')
    const calls: { url: string; auth: string | null }[] = []
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (url, init) => {
      calls.push({ url: String(url), auth: new Headers(init?.headers).get('authorization') })
      return new Response('x')
    })
    const saved: string[] = []
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { saved.push(this.download) })
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Reports' }))
    await userEvent.click(screen.getByRole('button', { name: 'PDF report' }))
    await userEvent.click(screen.getByRole('button', { name: 'Sample (ZIP)' }))
    await userEvent.click(await screen.findByRole('button', { name: /Pcap/ }))
    await waitFor(() => expect(saved).toEqual(['maldex-t1.pdf', `${'f'.repeat(64)}.zip`, 'abcdef0123.zip']))
    const downloads = calls.filter((c) => !c.url.endsWith('/image'))
    expect(downloads.map((c) => c.url)).toEqual(['/api/tasks/t1/report.pdf', '/api/tasks/t1/sample', '/api/artifacts/a1/download'])
    expect(downloads.every((c) => c.auth === 'Bearer tok')).toBe(true)
  })

  it('shows a failed download as an error toast', async () => {
    getTask.mockResolvedValue(task({ status: 'Completed', verdict: 'Malicious' }))
    vi.spyOn(globalThis, 'fetch').mockImplementation(async () => Response.json({ title: 'Report not found.' }, { status: 404 }))
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Reports' }))
    await userEvent.click(screen.getByRole('button', { name: 'PDF report' }))
    expect(await screen.findByText('Report not found.')).toBeInTheDocument()
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
    view.rerender(routes())
    expect(await screen.findByText('Completed')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Reports' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Time remaining')).toBeNull()

    // A push newer than the fetch wins again.
    live.value = { status: { ...stale, status: 'Failed', reason: 'x' }, processes: [], events: [], connected: true }
    view.rerender(routes())
    expect(await screen.findByText('Failed')).toBeInTheDocument()
  })

  it('does not show report links while running', async () => {
    getTask.mockResolvedValue(task({}))
    renderPage()
    await screen.findByText('Running')
    expect(screen.queryByRole('link', { name: 'HTML report' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Reports' })).toBeNull()
    expect(getTaskReport).not.toHaveBeenCalled()
  })

  it('switches from the live layout to the report when the task completes', async () => {
    getTask.mockResolvedValue(task({}))
    const view = renderPage()
    expect(await screen.findByLabelText('Live view')).toBeInTheDocument()

    getTaskReport.mockRejectedValueOnce(new ApiError(409, { type: 'report-not-ready', title: 'The report is only available once the task has completed.', status: 409 }))
    live.value = { status: { taskId: 't1', status: 'Completed', verdict: 'Malicious', deadlineAt: null }, processes: [], events: [], connected: true }
    view.rerender(routes())
    await waitFor(() => expect(getTaskReport).toHaveBeenCalledTimes(1))
    expect(screen.queryByRole('alert')).toBeNull()
    expect(screen.queryByRole('tab', { name: 'Overview' })).toBeNull()

    // The next status push retries.
    live.value = { status: { taskId: 't1', status: 'Completed', verdict: 'Malicious', deadlineAt: null }, processes: [], events: [], connected: true }
    view.rerender(routes())
    expect(await screen.findByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'true')
    expect(getTaskReport).toHaveBeenCalledTimes(2)
    expect(screen.queryByRole('alert')).toBeNull()
    expect(screen.queryByLabelText('Live view')).toBeNull()
  })

  it('shows a report load failure with Retry', async () => {
    getTask.mockResolvedValue(task({ status: 'Completed', verdict: 'Malicious' }))
    getTaskReport.mockRejectedValueOnce(new ApiError(500, { title: 'The report could not be built.' }))
    renderPage()
    expect(await screen.findByRole('alert')).toHaveTextContent('The report could not be built.')
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByRole('tab', { name: 'Overview' })).toBeInTheDocument()
  })

  it('shows the header line and a Resubmit link', async () => {
    getTask.mockResolvedValue(task({ submittedByName: 'Bob Analyst', ranSec: 125 }))
    renderPage()
    await screen.findByText('Running')
    const line = screen.getByLabelText('Task details')
    expect(line).toHaveTextContent('Acme')
    expect(line).toHaveTextContent('submitted by Bob Analyst')
    expect(line).toHaveTextContent('ran 2:05')
    expect(line).toHaveTextContent('Simulated')
    expect(screen.getByRole('link', { name: 'Resubmit…' })).toHaveAttribute('href', '/submit?from=t1')
  })

  it('shows the template provenance summary when the task has one', async () => {
    const summary = 'win11-golden v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)'
    getTask.mockResolvedValue(task({
      template: {
        id: 'win11-golden', displayName: 'Windows 11 Golden', versionNumber: 3, origin: 'Edit', sealedAt: '2026-10-24T00:00:00Z', summary,
      },
    }))
    renderPage()
    await screen.findByText('Running')
    expect(screen.getByText('Template')).toBeInTheDocument()
    expect(screen.getByText(summary)).toBeInTheDocument()
    expect(screen.queryByText('Image')).toBeNull()
  })

  it('re-fetches once when the task leaves Queued, so the bound template appears without a reload', async () => {
    const summary = 'win11-golden v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)'
    const bound = task({
      status: 'Preparing',
      template: { id: 'win11-golden', displayName: 'Windows 11 Golden', versionNumber: 3, origin: 'Edit', sealedAt: '2026-10-24T00:00:00Z', summary },
    })
    getTask.mockResolvedValue(task({ status: 'Queued', image: 'win11-golden', template: null, deadlineAt: null }))
    const view = renderPage()
    expect(await screen.findByText('Queued')).toBeInTheDocument()
    expect(screen.getByText('Image')).toBeInTheDocument()
    expect(getTask).toHaveBeenCalledTimes(1)

    const rerender = () =>
      view.rerender(routes())
    getTask.mockResolvedValue(bound)
    live.value = { status: { taskId: 't1', status: 'Preparing', verdict: 'Unknown', deadlineAt: null }, processes: [], events: [], connected: true }
    rerender()
    expect(await screen.findByText(summary)).toBeInTheDocument()
    expect(getTask).toHaveBeenCalledTimes(2)

    // Later pushes do not fetch again.
    getTask.mockResolvedValue(task({ ...bound, status: 'Running' }))
    live.value = { status: { taskId: 't1', status: 'Running', verdict: 'Unknown', deadlineAt: null }, processes: [], events: [], connected: true }
    rerender()
    expect(await screen.findByText('Running')).toBeInTheDocument()
    expect(getTask).toHaveBeenCalledTimes(2)
  })

  it('does not re-fetch on load for a task that was never seen Queued', async () => {
    getTask.mockResolvedValue(task({}))
    renderPage()
    await screen.findByText('Running')
    await new Promise((r) => setTimeout(r, 0))
    expect(getTask).toHaveBeenCalledTimes(1)
  })

  it('falls back to the image name for tasks that predate templates', async () => {
    getTask.mockResolvedValue(task({ image: 'win10-legacy', template: null }))
    renderPage()
    await screen.findByText('Running')
    expect(screen.getByText('Image')).toBeInTheDocument()
    expect(screen.getByText('win10-legacy')).toBeInTheDocument()
    expect(screen.queryByText('Template')).toBeNull()
  })
})
