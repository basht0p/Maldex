import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { useLocation } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import type { EventType, LiveEventDto } from '../../api/types'
import { renderWithKit } from '../../test/renderWithKit'
import { reportFixture } from '../../test/reportFixture'
import { EventsTab } from './EventsTab'

const ev = (id: number, type: EventType = 'File', summary = `s${id}`, processId = 1): LiveEventDto => ({
  id, type, at: '2026-10-19T00:00:00Z', pid: 100, processId, summary, dataJson: '{}',
})
const many = (from: number, n: number) => Array.from({ length: n }, (_, i) => ev(from + i))

let requests: URL[] = []
let respond: (u: URL) => LiveEventDto[] = () => []
const server = setupServer(
  http.get('/api/tasks/t1/events', ({ request }) => {
    const u = new URL(request.url)
    requests.push(u)
    return HttpResponse.json(respond(u))
  }),
)
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  requests = []
  respond = () => []
})
afterAll(() => server.close())

function Probe() {
  const loc = useLocation()
  return <output data-testid="location">{loc.pathname + loc.search}</output>
}
const renderTab = (route = '/tasks/t1', props: { live?: boolean; liveEvents?: LiveEventDto[] } = {}) =>
  renderWithKit(<><EventsTab taskId="t1" report={reportFixture()} {...props} /><Probe /></>, { route })
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

  it('builds export links from the current filters', async () => {
    renderTab('/tasks/t1?etype=Network&eq=evil')
    await waitFor(() => expect(requests.length).toBeGreaterThan(0))
    expect(screen.getByRole('link', { name: 'Export CSV' })).toHaveAttribute('href', '/api/tasks/t1/events/export?format=csv&type=Network&q=evil')
    expect(screen.getByRole('link', { name: 'Export JSON' })).toHaveAttribute('href', '/api/tasks/t1/events/export?format=json&type=Network&q=evil')
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
})
