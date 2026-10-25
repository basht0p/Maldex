import { fireEvent, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { useLocation } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import { HOSTILE, reportFixture, SHA256, taskFixture } from '../../test/reportFixture'
import { renderWithKit } from '../../test/renderWithKit'
import { TaskReport } from './TaskReport'

const server = setupServer(
  http.get('/api/tasks/t1/events', () => HttpResponse.json([])),
)
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
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
    expect(screen.queryByRole('link', { name: 'Download body (ZIP)' })).toBeNull()
    const row = screen.getByRole('button', { name: /payload\.bin/ })
    expect(row).toHaveAttribute('aria-expanded', 'false')
    await userEvent.click(row)
    expect(row).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText('Content-Type: application/octet-stream')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Download body (ZIP)' })).toHaveAttribute('href', '/api/tasks/t1/http/42/body')
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

  it('lists dropped files with a ZIP link', () => {
    renderReport(reportFixture(), '/tasks/t1?tab=files')
    expect(screen.getByText('C:\\Users\\a\\payload.dll')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy SHA-256' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /ZIP/ })).toHaveAttribute('href', '/api/artifacts/f1/download')
  })

  it('replaces a broken screenshot with a Not available tile', () => {
    renderReport()
    const imgs = screen.getAllByRole('img', { name: /^Screenshot at / })
    expect(imgs).toHaveLength(4)
    expect(imgs[0]).toHaveAttribute('src', '/api/artifacts/s0/image')
    expect(screen.getByRole('button', { name: '+2' })).toBeInTheDocument()
    fireEvent.error(imgs[0])
    expect(screen.getAllByRole('img', { name: /^Screenshot at / })).toHaveLength(3)
    expect(screen.getByText('Not available')).toBeInTheDocument()
  })

  it('shows events for a process in one URL update', async () => {
    renderReport(reportFixture(), '/tasks/t1?tab=processes')
    await userEvent.click(screen.getByRole('button', { name: 'Show events' }))
    expect(screen.getByTestId('location')).toHaveTextContent('/tasks/t1?tab=events&process=1')
    expect(screen.getByRole('tab', { name: /^Events/ })).toHaveAttribute('aria-selected', 'true')
  })
})
