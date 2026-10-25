import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { useLocation, useNavigate } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import type { EventType, LiveEventDto } from '../../api/types'
import { renderWithKit } from '../../test/renderWithKit'
import { reportFixture } from '../../test/reportFixture'
import type { ProcessOption } from './eventFilters'
import { EventsTab } from './EventsTab'

const ev = (id: number, type: EventType = 'File', summary = `s${id}`, processId = 1): LiveEventDto => ({
  id, type, at: '2026-10-19T00:00:00Z', pid: 100, processId, summary, dataJson: '{}',
})
const many = (from: number, n: number) => Array.from({ length: n }, (_, i) => ev(from + i))

let requests: URL[] = []
let gate: Promise<void> | null = null
let respond: (u: URL) => LiveEventDto[] = () => []
const server = setupServer(
  http.get('/api/tasks/t1/events', async ({ request }) => {
    const u = new URL(request.url)
    requests.push(u)
    if (gate && u.searchParams.has('afterId')) await gate
    return HttpResponse.json(respond(u))
  }),
)
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  requests = []
  gate = null
  respond = () => []
})
afterAll(() => server.close())

function Probe() {
  const loc = useLocation()
  return <output data-testid="location">{loc.pathname + loc.search}</output>
}
function Nav({ to }: { to: string }) {
  const navigate = useNavigate()
  return <button type="button" onClick={() => navigate(to)}>go</button>
}
const renderTab = (route = '/tasks/t1', props: { live?: boolean; liveEvents?: LiveEventDto[]; liveProcesses?: ProcessOption[] } = {}) =>
  renderWithKit(<><EventsTab taskId="t1" report={reportFixture()} {...props} /><Probe /><Nav to="/tasks/t1?eq=other" /></>, { route })
const last = () => requests[requests.length - 1]

describe('EventsTab', () => {
  it('sends type, process and text filters and keeps them in the URL', async () => {
    respond = () => [ev(1)]
    renderTab()
    await screen.findByText('s1')
    expect(last().searchParams.get('take')).toBe('500')
    await userEvent.click(screen.getByRole('button', { name: 'Network' }))
    await waitFor(() => expect(last().searchParams.get('type')).toBe('Network'))
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Process' }), '1')
    await waitFor(() => expect(last().searchParams.get('processId')).toBe('1'))
    await userEvent.type(screen.getByRole('searchbox', { name: /search/i }), 'evil')
    await waitFor(() => expect(last().searchParams.get('q')).toBe('evil'))
    const loc = screen.getByTestId('location').textContent!
    expect(loc).toContain('etype=Network')
    expect(loc).toContain('process=1')
    expect(loc).toContain('eq=evil')
    // whitespace-only search is not sent
    await userEvent.clear(screen.getByRole('searchbox', { name: /search/i }))
    await userEvent.type(screen.getByRole('searchbox', { name: /search/i }), '   ')
    await waitFor(() => expect(screen.getByTestId('location').textContent).toContain('eq=+++'))
    await waitFor(() => expect(last().searchParams.has('q')).toBe(false))
  })

  it('loads more in pages of 500', async () => {
    respond = (u) => (u.searchParams.get('afterId') === '500' ? many(501, 20) : many(1, 500))
    renderTab()
    await userEvent.click(await screen.findByRole('button', { name: 'Load more' }))
    await waitFor(() => expect(last().searchParams.get('afterId')).toBe('500'))
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull())
  })

  it('exports with the current filters', async () => {
    const exported: string[] = []
    server.use(http.get('/api/tasks/t1/events/export', ({ request }) => {
      const u = new URL(request.url)
      exported.push(u.pathname + u.search)
      return new HttpResponse('[]')
    }))
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    renderTab('/tasks/t1?etype=Network&eq=evil')
    await waitFor(() => expect(requests.length).toBeGreaterThan(0))
    await userEvent.click(screen.getByRole('button', { name: 'Export CSV' }))
    await userEvent.click(screen.getByRole('button', { name: 'Export JSON' }))
    await waitFor(() => expect(exported).toEqual([
      '/api/tasks/t1/events/export?format=csv&type=Network&q=evil',
      '/api/tasks/t1/events/export?format=json&type=Network&q=evil',
    ]))
    vi.restoreAllMocks()
  })

  it('matches the live text filter against the event data too, like the server', async () => {
    const events = [
      { ...ev(1, 'Registry', 'set value'), dataJson: '{"key":"HKCU\\\\Run\\\\EvilStartup"}' },
      { ...ev(2, 'Registry', 'set value'), dataJson: '{"key":"HKCU\\\\Run\\\\Benign"}' },
    ]
    renderTab('/tasks/t1?eq=evilstartup', { live: true, liveEvents: events })
    expect(await screen.findAllByText('set value')).toHaveLength(1) // only the event whose data matches
  })

  it('filters live events client-side by the same rules', async () => {
    const events = [ev(1, 'File', 'write Evil.dll', 1), ev(2, 'Network', 'connect EVIL.example', 1), ev(3, 'Network', 'connect ok', 2), ev(4, 'Network', 'connect evil', 2)]
    renderTab('/tasks/t1?etype=Network&process=2&eq=evil', { live: true, liveEvents: events })
    expect(await screen.findByText('connect evil')).toBeInTheDocument()
    expect(screen.queryByText('connect EVIL.example')).toBeNull()
    expect(screen.queryByText('connect ok')).toBeNull()
    expect(screen.queryByText('write Evil.dll')).toBeNull()
    expect(requests).toHaveLength(0)
  })

  it('opens filtered to a process from the Processes tab', async () => {
    renderTab('/tasks/t1?tab=events&process=7')
    await waitFor(() => expect(last()?.searchParams.get('processId')).toBe('7'))
    expect(screen.getByRole('combobox', { name: 'Process' })).toHaveValue('7')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Process' }), '')
    await waitFor(() => expect(last().searchParams.has('processId')).toBe(false))
    expect(screen.getByTestId('location').textContent).not.toContain('process=')
  })
  it('drops a Load more response that arrives after the filters changed', async () => {
    respond = (u) => (u.searchParams.has('afterId') ? [ev(9001, 'File', 'STALE')] : u.searchParams.get('type') === 'Dns' ? [ev(1, 'Dns', 'fresh')] : many(1, 500))
    let release!: () => void
    gate = new Promise<void>((r) => { release = r })
    renderTab()
    await userEvent.click(await screen.findByRole('button', { name: 'Load more' }))
    await waitFor(() => expect(requests.some((u) => u.searchParams.has('afterId'))).toBe(true))
    await userEvent.click(screen.getByRole('button', { name: 'Dns' }))
    await screen.findByText('fresh')
    release()
    await new Promise((r) => setTimeout(r, 50))
    expect(screen.queryByText('STALE')).toBeNull()
    expect(screen.getByText('fresh')).toBeInTheDocument()
  })

  it('takes an external eq change into the input and does not overwrite it', async () => {
    respond = () => [ev(1)]
    renderTab('/tasks/t1?eq=first')
    const box = screen.getByRole('searchbox', { name: /search/i })
    expect(box).toHaveValue('first')
    await userEvent.click(screen.getByRole('button', { name: 'go' }))
    await waitFor(() => expect(box).toHaveValue('other'))
    await new Promise((r) => setTimeout(r, 400))
    expect(screen.getByTestId('location').textContent).toContain('eq=other')
    expect(box).toHaveValue('other')
    expect(box).toHaveAttribute('data-search')
  })

  it('offers the live process tree in live mode', async () => {
    renderTab('/tasks/t1', { live: true, liveEvents: [], liveProcesses: [{ id: 5, pid: 555, imagePath: 'C:\\x\\live.exe' }] })
    expect(await screen.findByRole('option', { name: '555 live.exe' })).toBeInTheDocument()
  })

  it('says when the filters match nothing', async () => {
    respond = () => []
    renderTab('/tasks/t1?etype=Dns')
    expect(await screen.findByText('No events match these filters')).toBeInTheDocument()
  })
})
