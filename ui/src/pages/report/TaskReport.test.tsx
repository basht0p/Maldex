import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { useLocation } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { HOSTILE, reportFixture, SHA256, taskFixture } from '../../test/reportFixture'
import { renderWithKit } from '../../test/renderWithKit'
import { TaskReport } from './TaskReport'

const PNG = new Uint8Array([0x89, 0x50, 0x4e, 0x47])
const server = setupServer(
  http.get('/api/tasks/t1/events', () => HttpResponse.json([])),
  http.get('/api/artifacts/:id/image', ({ params }) =>
    params.id === 's1' ? HttpResponse.json({ title: 'Unauthorized' }, { status: 401 }) : new HttpResponse(PNG, { headers: { 'Content-Type': 'image/png' } })),
)
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.restoreAllMocks()
})

/** Captures the file names that authenticated downloads saved. */
function captureDownloads() {
  const saved: string[] = []
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { saved.push(this.download) })
  return saved
}
afterAll(() => server.close())

function LocationProbe() {
  const loc = useLocation()
  return <output data-testid="location">{loc.pathname + loc.search}</output>
}

const renderReport = (report = reportFixture(), route = '/tasks/t1') =>
  renderWithKit(<><TaskReport report={report} taskId="t1" /><LocationProbe /></>, { route })

describe('TaskReport', () => {
  it('opens on Overview with counted tab labels', () => {
    renderReport()
    expect(screen.getByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'true')
    for (const name of ['Detections 7', 'Processes 1', 'Network 3', 'HTTP 1', 'Files 1', 'Screenshots 6'])
      expect(screen.getByRole('tab', { name })).toBeInTheDocument()
  })

  it('lists the top five detections and links to the rest', async () => {
    renderReport()
    const card = screen.getByRole('region', { name: 'Why Malicious' })
    const rules = within(card).getAllByRole('listitem').map((li) => li.textContent)
    expect(rules).toHaveLength(5)
    expect(rules[0]).toContain('Rule_Sev9')
    expect(rules[4]).toContain('Rule_Sev3')
    expect(within(card).queryByText('Rule_Sev2')).toBeNull()
    await userEvent.click(within(card).getByRole('button', { name: 'All 7 detections' }))
    expect(screen.getByRole('tab', { name: 'Detections 7' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('location')).toHaveTextContent('tab=detections')
  })

  it('says No detections when there are none', () => {
    renderReport(reportFixture({ detections: [], task: taskFixture({ verdict: 'NoThreats' }) }))
    const card = screen.getByRole('region', { name: 'Why No threats detected' })
    expect(within(card).getByText('No detections')).toBeInTheDocument()
  })

  it('renders hostile strings as text', async () => {
    const { container } = renderReport()
    await userEvent.click(screen.getByRole('tab', { name: 'Detections 7' }))
    await userEvent.click(screen.getByRole('button', { name: 'Rule_Sev3' }))
    expect(screen.getByText(HOSTILE)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'HTTP 1' }))
    await userEvent.click(screen.getByRole('button', { name: /payload\.bin/ }))
    expect(screen.getByText(`User-Agent: ${HOSTILE}`)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Processes 1' }))
    expect(screen.getByText(`dropper.exe ${HOSTILE}`)).toBeInTheDocument()
    expect(container.querySelector('script')).toBeNull()
  })

  it('shows sample hashes with copy buttons', () => {
    renderReport()
    const card = screen.getByRole('region', { name: 'Sample' })
    expect(within(card).getByText(SHA256)).toBeInTheDocument()
    for (const name of ['Copy SHA-256', 'Copy SHA-1', 'Copy MD5']) expect(within(card).getByRole('button', { name })).toBeInTheDocument()
    expect(within(card).getByText('2.0 KiB')).toBeInTheDocument()
    expect(within(card).getByText('PE32 executable')).toBeInTheDocument()
  })

  it('shows a URL card instead of hashes for a URL task', () => {
    const url = 'http://phish.example/login'
    renderReport(reportFixture({
      task: taskFixture({ sampleSha256: null, originalFileName: null, url }),
      sha1: null, md5: null, sampleSize: null, fileType: null, samePrevious: { count: 0, latest: null },
    }))
    expect(screen.queryByRole('region', { name: 'Sample' })).toBeNull()
    const card = screen.getByRole('region', { name: 'URL' })
    expect(within(card).getByText(url)).toBeInTheDocument()
    expect(within(card).getByRole('button', { name: 'Copy URL' })).toBeInTheDocument()
    expect(within(card).queryByText(/SHA-256/)).toBeNull()
  })

  it('links other tasks with the same hash', () => {
    renderReport()
    expect(screen.getByRole('link', { name: '2 other tasks with this hash' })).toHaveAttribute('href', `/?q=${SHA256}`)
  })

  it('hides the same-hash link when there are none', () => {
    renderReport(reportFixture({ samePrevious: { count: 0, latest: null } }))
    expect(screen.queryByRole('link', { name: /with this hash/ })).toBeNull()
  })

  it('lists up to five contacted hosts with copy buttons', () => {
    renderReport()
    const card = screen.getByRole('region', { name: 'Contacted' })
    const items = within(card).getAllByRole('listitem').map((li) => li.textContent)
    expect(items[0]).toContain('evil.example')
    expect(items[1]).toContain('93.184.216.34:443')
    expect(items).toHaveLength(4)
    expect(within(card).getAllByRole('button', { name: /^Copy/ })).toHaveLength(4)
  })

  it('shows the status timeline with time spent in each status', () => {
    renderReport()
    const card = screen.getByRole('region', { name: 'Status timeline' })
    const items = within(card).getAllByRole('listitem')
    expect(items).toHaveLength(3)
    expect(items[0]).toHaveTextContent('Queued')
    expect(items[0]).toHaveTextContent('0:10')
    expect(items[1]).toHaveTextContent('5:00')
  })

  it('keeps the active tab in the URL', async () => {
    const first = renderReport()
    await userEvent.click(screen.getByRole('tab', { name: 'HTTP 1' }))
    expect(screen.getByTestId('location')).toHaveTextContent('/tasks/t1?tab=http')
    first.unmount()
    renderReport(reportFixture(), '/tasks/t1?tab=http')
    expect(screen.getByRole('tab', { name: 'HTTP 1' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('columnheader', { name: 'Method' })).toBeInTheDocument()
  })

  it('expands an HTTP row and offers the body download', async () => {
    renderReport(reportFixture(), '/tasks/t1?tab=http')
    expect(screen.queryByRole('button', { name: 'Download body (ZIP)' })).toBeNull()
    const row = screen.getByRole('button', { name: /payload\.bin/ })
    expect(row).toHaveAttribute('aria-expanded', 'false')
    await userEvent.click(row)
    expect(row).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText('Content-Type: application/octet-stream')).toBeInTheDocument()
    let hit = false
    server.use(http.get('/api/tasks/t1/http/42/body', () => { hit = true; return new HttpResponse('PK') }))
    const saved = captureDownloads()
    await userEvent.click(screen.getByRole('button', { name: 'Download body (ZIP)' }))
    await waitFor(() => expect(saved).toEqual(['http-42.zip']))
    expect(hit).toBe(true)
  })

  it('shows a failed download as an error toast', async () => {
    server.use(http.get('/api/tasks/t1/http/42/body', () => HttpResponse.json({ title: 'The body is no longer stored.' }, { status: 404 })))
    renderReport(reportFixture(), '/tasks/t1?tab=http')
    await userEvent.click(screen.getByRole('button', { name: /payload\.bin/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Download body (ZIP)' }))
    expect(await screen.findByText('The body is no longer stored.')).toBeInTheDocument()
  })

  it('shows the truncation notice', () => {
    renderReport(reportFixture({ truncated: { processes: false, networkFlows: true, dns: false, http: false } }), '/tasks/t1?tab=network')
    expect(screen.getAllByText('Showing the first 2,000. Download the full report for the rest.')).toHaveLength(1)
  })

  it('sorts detections by severity, descending first', async () => {
    renderReport(reportFixture(), '/tasks/t1?tab=detections')
    const firstRule = () => within(screen.getAllByRole('row')[1]).getByRole('button').textContent
    expect(firstRule()).toBe('Rule_Sev9')
    await userEvent.click(screen.getByRole('button', { name: 'Severity' }))
    expect(firstRule()).toBe('Rule_Sev1')
    await userEvent.click(screen.getByRole('button', { name: 'Rule' }))
    expect(firstRule()).toBe('Rule_Sev9')
  })

  it('lists dropped files with an authenticated ZIP download', async () => {
    server.use(http.get('/api/artifacts/f1/download', () => new HttpResponse('PK')))
    renderReport(reportFixture(), '/tasks/t1?tab=files')
    expect(screen.getByText('C:\\Users\\a\\payload.dll')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy SHA-256' })).toBeInTheDocument()
    const saved = captureDownloads()
    await userEvent.click(screen.getByRole('button', { name: /ZIP/ }))
    await waitFor(() => expect(saved).toEqual([`${'d'.repeat(64)}.zip`]))
  })

  it('exports events through an authenticated download', async () => {
    let search = ''
    server.use(http.get('/api/tasks/t1/events/export', ({ request }) => { search = new URL(request.url).search; return new HttpResponse('id') }))
    renderReport(reportFixture(), '/tasks/t1?tab=events')
    const saved = captureDownloads()
    await userEvent.click(screen.getByRole('button', { name: 'Export CSV' }))
    await waitFor(() => expect(saved).toEqual(['task-t1-events.csv']))
    expect(new URLSearchParams(search).get('format')).toBe('csv')
    await userEvent.click(screen.getByRole('button', { name: 'Export JSON' }))
    await waitFor(() => expect(saved).toEqual(['task-t1-events.csv', 'task-t1-events.json']))
  })

  it('loads screenshots as plain images and shows Not available when one fails to load', async () => {
    renderReport()
    expect(screen.getByRole('button', { name: '+2' })).toBeInTheDocument()
    const imgs = screen.getAllByRole('img', { name: /^Screenshot at / })
    expect(imgs).toHaveLength(4)
    for (const img of imgs) expect(img.getAttribute('src')).toMatch(/^\/api\/artifacts\/.+\/image$/)
    fireEvent.error(imgs[0])
    expect(screen.getAllByText('Not available')).toHaveLength(1)
    expect(screen.getAllByRole('img', { name: /^Screenshot at / })).toHaveLength(3)
  })

  it('shows processing warnings on the Overview as text, and nothing when there are none', () => {
    const first = renderReport(reportFixture({ task: taskFixture({ processingWarnings: [`YARA timed out ${HOSTILE}`, 'Suricata skipped'] }) }))
    const panel = screen.getByRole('region', { name: 'Processing warnings' })
    expect(within(panel).getByText(`YARA timed out ${HOSTILE}`)).toBeInTheDocument()
    expect(within(panel).getAllByRole('listitem')).toHaveLength(2)
    expect(first.container.querySelector('script')).toBeNull()
    first.unmount()
    renderReport()
    expect(screen.queryByRole('region', { name: 'Processing warnings' })).toBeNull()
  })

  it('lists detection tags as chips', () => {
    const detections = reportFixture().detections.map((d, i) => (i === 1 ? { ...d, tags: ['ransomware', HOSTILE] } : d))
    renderReport(reportFixture({ detections }), '/tasks/t1?tab=detections')
    expect(screen.getByRole('columnheader', { name: 'Tags' })).toBeInTheDocument()
    const row = screen.getAllByRole('row')[1] // Rule_Sev9 sorts first
    expect(within(row).getByText('ransomware')).toBeInTheDocument()
    expect(within(row).getByText(HOSTILE)).toBeInTheDocument()
  })

  it('offers copy buttons for flow endpoints, DNS values and HTTP host and URL', async () => {
    const first = renderReport(reportFixture(), '/tasks/t1?tab=network')
    expect(screen.getAllByRole('button', { name: 'Copy source' })).toHaveLength(2)
    expect(screen.getAllByRole('button', { name: 'Copy destination' })).toHaveLength(2)
    expect(screen.getByRole('button', { name: 'Copy query' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy answers' })).toBeInTheDocument()
    first.unmount()
    renderReport(reportFixture(), '/tasks/t1?tab=http')
    expect(screen.getByRole('button', { name: 'Copy host' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy URL' })).toBeInTheDocument()
  })

  it('shows events for a process in one URL update', async () => {
    renderReport(reportFixture(), '/tasks/t1?tab=processes')
    await userEvent.click(screen.getByRole('button', { name: 'Show events' }))
    expect(screen.getByTestId('location')).toHaveTextContent('/tasks/t1?tab=events&process=1')
    expect(screen.getByRole('tab', { name: /^Events/ })).toHaveAttribute('aria-selected', 'true')
  })
})
