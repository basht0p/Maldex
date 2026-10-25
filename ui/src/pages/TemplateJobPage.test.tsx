import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MeDto, TemplateJobDto } from '../api/types'

const guac = vi.hoisted(() => {
  const state = { tunnelUrls: [] as string[], connects: [] as string[], disconnects: 0 }
  class Client {
    onerror: ((s: { message?: string }) => void) | null = null
    private el = document.createElement('div')
    constructor(_tunnel: unknown) {
      this.el.setAttribute('data-testid', 'guac-display')
    }
    getDisplay() {
      return { getElement: () => this.el, scale: vi.fn(), onresize: null }
    }
    connect(data: string) { state.connects.push(data) }
    disconnect() { state.disconnects++ }
    sendMouseState = vi.fn()
    sendKeyEvent = vi.fn()
  }
  class WebSocketTunnel { constructor(url: string) { state.tunnelUrls.push(url) } }
  class Mouse { constructor(_el: Element) {} onEach() {} }
  class Keyboard { constructor(_el: Element) {} reset() {} }
  return { state, default: { Client, WebSocketTunnel, Mouse, Keyboard } }
})
vi.mock('guacamole-common-js', () => ({ default: guac.default }))

const getTemplateJob = vi.fn()
const sealTemplateJob = vi.fn()
const cancelTemplateJob = vi.fn()
const getTemplateJobTunnelToken = vi.fn()
const fetchBlob = vi.fn()
const downloadFile = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  getTemplateJob: (...a: unknown[]) => getTemplateJob(...a),
  sealTemplateJob: (...a: unknown[]) => sealTemplateJob(...a),
  cancelTemplateJob: (...a: unknown[]) => cancelTemplateJob(...a),
  getTemplateJobTunnelToken: (...a: unknown[]) => getTemplateJobTunnelToken(...a),
  fetchBlob: (...a: unknown[]) => fetchBlob(...a),
  downloadFile: (...a: unknown[]) => downloadFile(...a),
}))

import { ApiError } from '../api/client'
import type { HubConnectionLike } from '../live/useTaskLive'
import { renderWithKit } from '../test/renderWithKit'
import TemplateJobPage from './TemplateJobPage'

class MockHub implements HubConnectionLike {
  handlers = new Map<string, (...args: never[]) => void>()
  calls: string[] = []
  stopped = false
  on(name: string, cb: (...args: never[]) => void) { this.handlers.set(name, cb) }
  onreconnecting() {}
  onreconnected() {}
  onclose() {}
  async start() { this.calls.push('start') }
  async stop() { this.stopped = true }
  async invoke(method: string, ...args: unknown[]) { this.calls.push(`${method}:${args.join(',')}`) }
  push(dto: TemplateJobDto) { act(() => { (this.handlers.get('templateJob') as (p: unknown) => void)(dto) }) }
}

const admin: MeDto = { id: 'u1', displayName: 'Dev', role: 'Admin', maxUploadBytes: 268_435_456 }
const job = (over: Partial<TemplateJobDto> = {}): TemplateJobDto => ({
  id: 'job-1', kind: 'Edit', templateId: 'win11-golden', baseVersionNumber: 3, resultVersionNumber: null, network: 'None', state: 'Queued',
  message: null, failureReason: null, createdAt: '2026-10-24T10:00:00Z', endedAt: null, idleDeadline: null, ...over,
})

let hub: MockHub
function renderJob(me: MeDto | null = admin) {
  return renderWithKit(
    <Routes>
      <Route path="/admin/template-jobs/:id" element={<TemplateJobPage me={me} createConnection={() => hub} />} />
    </Routes>,
    { route: '/admin/template-jobs/job-1' },
  )
}
const current = () => screen.getByRole('list', { name: 'Progress' }).querySelector('[aria-current="step"]')

beforeEach(() => {
  vi.clearAllMocks()
  guac.state.tunnelUrls.length = 0
  guac.state.connects.length = 0
  guac.state.disconnects = 0
  hub = new MockHub()
  getTemplateJob.mockResolvedValue(job())
  getTemplateJobTunnelToken.mockResolvedValue({ token: 'tok-9' })
})
let confirmSpy: ReturnType<typeof vi.spyOn>
beforeEach(() => { confirmSpy = vi.spyOn(window, 'confirm') })
afterEach(() => {
  expect(confirmSpy).not.toHaveBeenCalled() // confirmations are in-app dialogs, never window.confirm
  vi.restoreAllMocks()
})

describe('TemplateJobPage', () => {
  it('tells non-admins that administrator access is required, without opening the hub', async () => {
    renderJob({ ...admin, role: 'Analyst' })
    expect(await screen.findByRole('alert')).toHaveTextContent(/administrator access is required/i)
    expect(hub.calls).toEqual([])
  })

  it('joins the job group on the hub, loads the job and shows the Edit step tracker', async () => {
    renderJob()
    await screen.findByRole('heading', { name: /Edit/ })
    expect(hub.calls).toContain('JoinTemplateJob:job-1')
    expect(getTemplateJob).toHaveBeenCalledWith('job-1')
    const steps = within(screen.getByRole('list', { name: 'Progress' })).getAllByRole('listitem').map((li) => li.textContent?.trim())
    expect(steps).toEqual(['Queued', 'Preparing', 'Configuring', 'Sealing', 'Verifying', 'Published'])
    expect(current()).toHaveTextContent('Queued')
  })

  it('a Refresh hides the Configuring step', async () => {
    getTemplateJob.mockResolvedValue(job({ kind: 'Refresh', state: 'Preparing' }))
    renderJob()
    await screen.findByRole('heading', { name: /Refresh components/ })
    const steps = within(screen.getByRole('list', { name: 'Progress' })).getAllByRole('listitem').map((li) => li.textContent?.trim())
    expect(steps).toEqual(['Queued', 'Preparing', 'Sealing', 'Verifying', 'Published'])
    expect(current()).toHaveTextContent('Preparing')
  })

  it('the tracker and message follow hub updates', async () => {
    renderJob()
    await screen.findByRole('heading', { name: /Edit/ })
    hub.push(job({ state: 'Preparing', message: 'Copying the base disk' }))
    expect(current()).toHaveTextContent('Preparing')
    expect(screen.getByText('Copying the base disk')).toBeInTheDocument()
    hub.push(job({ state: 'Sealing', message: 'Sysprep running' }))
    expect(current()).toHaveTextContent('Sealing')
    expect(screen.getByText('Sysprep running')).toBeInTheDocument()
    expect(screen.queryByText('Copying the base disk')).toBeNull()
  })

  it('ignores updates for another job', async () => {
    renderJob()
    await screen.findByRole('heading', { name: /Edit/ })
    hub.push(job({ id: 'other', state: 'Verifying' }))
    expect(current()).toHaveTextContent('Queued')
  })

  it('a push that lands before the REST load finishes wins over the load', async () => {
    let resolve!: (j: TemplateJobDto) => void
    getTemplateJob.mockReturnValue(new Promise<TemplateJobDto>((r) => { resolve = r }))
    renderJob()
    await waitFor(() => expect(hub.handlers.has('templateJob')).toBe(true))
    await waitFor(() => expect(hub.calls).toContain('JoinTemplateJob:job-1'))
    hub.push(job({ state: 'Sealing' }))
    await act(async () => { resolve(job({ state: 'Queued' })) })
    expect(current()).toHaveTextContent('Sealing')
  })

  it('shows elapsed time, stopped at the end time once the job has ended', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Published', createdAt: '2026-10-24T10:00:00Z', endedAt: '2026-10-24T11:02:03Z' }))
    renderJob()
    expect(await screen.findByText('Elapsed 1:02:03')).toBeInTheDocument()
  })

  it('shows no console and no Seal in the states before Configuring, and offers Cancel', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Preparing' }))
    renderJob()
    await screen.findByRole('heading', { name: /Edit/ })
    expect(screen.queryByLabelText('Live view')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Seal' })).toBeNull()
    expect(getTemplateJobTunnelToken).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(cancelTemplateJob).toHaveBeenCalledWith('job-1')
  })

  it('in Configuring shows the console on the job tunnel, the network badge and the idle deadline', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Configuring', idleDeadline: '2026-10-24T12:00:00Z' }))
    renderJob()
    expect(await screen.findByTestId('guac-display')).toBeInTheDocument()
    expect(getTemplateJobTunnelToken).toHaveBeenCalledWith('job-1')
    expect(guac.state.tunnelUrls[0]).toMatch(/^wss?:\/\/[^/]+\/api\/admin\/template-jobs\/job-1\/tunnel$/)
    expect(guac.state.connects[0]).toContain('token=tok-9')
    expect(screen.getByText('None')).toBeInTheDocument()
    expect(screen.getByText(/Discarded in .* if the console stays closed/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Seal' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Discard' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull()
  })

  it('the Direct badge carries the text Direct: internet on', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Configuring', network: 'Direct' }))
    renderJob()
    expect(await screen.findByText('Direct: internet on')).toBeInTheDocument()
    expect(screen.queryByText('None')).toBeNull()
  })

  it('the console appears when the job reaches Configuring and goes when it leaves', async () => {
    renderJob()
    await screen.findByRole('heading', { name: /Edit/ })
    expect(screen.queryByLabelText('Live view')).toBeNull()
    hub.push(job({ state: 'Configuring' }))
    expect(await screen.findByTestId('guac-display')).toBeInTheDocument()
    hub.push(job({ state: 'Sealing' }))
    await waitFor(() => expect(screen.queryByLabelText('Live view')).toBeNull())
    expect(guac.state.disconnects).toBe(1)
    expect(screen.queryByRole('button', { name: 'Seal' })).toBeNull()
  })

  it('Seal sends the optional note, or none', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Configuring' }))
    sealTemplateJob.mockResolvedValue(undefined)
    renderJob()
    await screen.findByRole('button', { name: 'Seal' })
    await userEvent.click(screen.getByRole('button', { name: 'Seal' }))
    expect(sealTemplateJob).toHaveBeenLastCalledWith('job-1', '')
  })

  it('Seal trims the note', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Configuring' }))
    sealTemplateJob.mockResolvedValue(undefined)
    renderJob()
    await screen.findByRole('button', { name: 'Seal' })
    await userEvent.type(screen.getByLabelText('Seal note (optional)'), ' Installed 7-Zip ')
    await userEvent.click(screen.getByRole('button', { name: 'Seal' }))
    expect(sealTemplateJob).toHaveBeenLastCalledWith('job-1', 'Installed 7-Zip')
  })

  it('Discard asks first and then cancels the job', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Configuring' }))
    cancelTemplateJob.mockResolvedValue(undefined)
    renderJob()
    await screen.findByRole('button', { name: 'Discard' })
    await userEvent.click(screen.getByRole('button', { name: 'Discard' }))
    const dlg = await screen.findByRole('dialog', { name: 'Discard this job?' })
    expect(dlg).toHaveTextContent('The working copy is deleted and no version is created.')
    await userEvent.click(within(dlg).getByRole('button', { name: 'Cancel' }))
    expect(cancelTemplateJob).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Discard' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Discard' }))
    expect(cancelTemplateJob).toHaveBeenCalledWith('job-1')
    expect(await screen.findByText('Job discarded')).toBeInTheDocument()
  })

  it('shows a 409 reason from Seal inline and keeps the console', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Configuring' }))
    sealTemplateJob.mockRejectedValue(new ApiError(409, { title: 'The job is not in the Configuring state.', status: 409 }))
    renderJob()
    await userEvent.click(await screen.findByRole('button', { name: 'Seal' }))
    expect(await screen.findByText('The job is not in the Configuring state.')).toBeInTheDocument()
    expect(screen.getByTestId('guac-display')).toBeInTheDocument()
  })

  it('shows a 409 reason from Cancel inline', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Verifying' }))
    cancelTemplateJob.mockRejectedValue(new ApiError(409, { title: 'Too late to cancel.', status: 409 }))
    renderJob()
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }))
    expect(await screen.findByText('Too late to cancel.')).toBeInTheDocument()
  })

  it.each(['Failed', 'Cancelled'] as const)('%s shows the reason and no actions', async (state) => {
    getTemplateJob.mockResolvedValue(job({ state, failureReason: 'The disk filled up.', endedAt: '2026-10-24T10:30:00Z' }))
    renderJob()
    expect(await screen.findByText(`${state}: The disk filled up.`)).toBeInTheDocument()
    expect(current()).toBeNull()
    expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull()
    expect(screen.queryByLabelText('Live view')).toBeNull()
  })

  it('Sealed (held) says the version is not published', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Sealed', resultVersionNumber: 4, endedAt: '2026-10-24T10:30:00Z' }))
    renderJob()
    expect(await screen.findByText(/Sealed and held: this version is not published/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull()
  })

  it('after Seal is accepted it says "Seal requested" and disables Seal and the note until the state changes', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Configuring' }))
    sealTemplateJob.mockResolvedValue(undefined)
    renderJob()
    await userEvent.click(await screen.findByRole('button', { name: 'Seal' }))
    expect(await screen.findByText('Seal requested')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Seal' })).toBeDisabled()
    expect(screen.getByLabelText('Seal note (optional)')).toBeDisabled()
    hub.push(job({ state: 'Sealing' }))
    expect(screen.queryByText('Seal requested')).toBeNull()
  })

  it('a failed Seal does not mark the seal as requested', async () => {
    getTemplateJob.mockResolvedValue(job({ state: 'Configuring' }))
    sealTemplateJob.mockRejectedValue(new ApiError(409, { title: 'Nope.', status: 409 }))
    renderJob()
    await userEvent.click(await screen.findByRole('button', { name: 'Seal' }))
    await screen.findByText('Nope.')
    expect(screen.queryByText('Seal requested')).toBeNull()
    expect(screen.getByRole('button', { name: 'Seal' })).toBeEnabled()
  })

  it('leaves the hub when it unmounts', async () => {
    const { unmount } = renderJob()
    await screen.findByRole('heading', { name: /Edit/ })
    unmount()
    expect(hub.stopped).toBe(true)
  })

  it('shows a load error when the job cannot be loaded', async () => {
    getTemplateJob.mockRejectedValue(new ApiError(404, { title: 'Not found' }))
    renderJob()
    expect(await screen.findByRole('alert')).toHaveTextContent('Not found')
  })

  describe('Task 6', () => {
    const t = (m: number, sec = 0) => new Date(Date.UTC(2026, 9, 24, 10, m, sec)).toISOString()
    const withSteps = (over: Partial<TemplateJobDto> = {}) => job({
      state: 'Configuring', createdAt: t(0),
      steps: [{ state: 'Preparing', at: t(1), message: null }, { state: 'Configuring', at: t(3), message: null }], ...over,
    })
    const timeline = () => within(screen.getByRole('list', { name: 'Timeline' })).getAllByRole('listitem')

    afterEach(() => vi.useRealTimers())

    it('shows Queued then each step with durations and ticks the current one', async () => {
      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'], now: new Date(t(5)) })
      getTemplateJobTunnelToken.mockReturnValue(new Promise(() => undefined))
      getTemplateJob.mockResolvedValue(withSteps())
      renderJob()
      await screen.findByRole('list', { name: 'Timeline' })
      expect(timeline().map((li) => li.textContent)).toEqual([
        expect.stringMatching(/^Queued.*1:00$/), expect.stringMatching(/^Preparing.*2:00$/), expect.stringMatching(/^Configuring.*2:00$/),
      ])
      act(() => { vi.advanceTimersByTime(5000) })
      expect(timeline()[2].textContent).toMatch(/2:05$/)
    })

    it('a terminal step has no duration', async () => {
      getTemplateJob.mockResolvedValue(withSteps({ state: 'Published', steps: [{ state: 'Preparing', at: t(1) }, { state: 'Published', at: t(3) }] }))
      renderJob()
      await screen.findByRole('list', { name: 'Timeline' })
      expect(timeline()[2].textContent).toMatch(/^Published/)
      expect(timeline()[2].textContent).not.toMatch(/\d:\d\d$/)
    })

    it('keeps held steps and the requester when a push arrives without them', async () => {
      getTemplateJob.mockResolvedValue(withSteps({ requestedByName: 'Ada' }))
      renderJob()
      await screen.findByText('Requested by Ada')
      hub.push(job({ state: 'Sealing', message: 'Sysprep', createdAt: t(0), requestedByName: null }))
      expect(current()).toHaveTextContent('Sealing')
      expect(timeline()).toHaveLength(3)
      expect(screen.getByText('Requested by Ada')).toBeInTheDocument()
    })

    it('shows only the current state for a job without steps', async () => {
      getTemplateJob.mockResolvedValue(job({ state: 'Preparing' }))
      renderJob()
      expect(await screen.findByText(/^Preparing since/)).toBeInTheDocument()
      expect(screen.queryByRole('list', { name: 'Timeline' })).toBeNull()
    })

    it('expands and restores the console layout', async () => {
      getTemplateJob.mockResolvedValue(job({ state: 'Configuring' }))
      renderJob()
      const btn = await screen.findByRole('button', { name: 'Expand' })
      const panel = btn.closest('div[class*="panel"]') as HTMLElement
      expect(panel.className).not.toMatch(/expanded/)
      await userEvent.click(btn)
      expect(panel.className).toMatch(/expanded/)
      await userEvent.click(screen.getByRole('button', { name: 'Exit' }))
      expect(panel.className).not.toMatch(/expanded/)
      expect(screen.getByRole('button', { name: 'Expand' })).toBeInTheDocument()
    })

    it('requests fullscreen when available', async () => {
      const req = vi.fn().mockResolvedValue(undefined)
      Object.defineProperty(document.documentElement, 'requestFullscreen', { value: req, configurable: true })
      try {
        getTemplateJob.mockResolvedValue(job({ state: 'Configuring' }))
        renderJob()
        await userEvent.click(await screen.findByRole('button', { name: 'Expand' }))
        expect(req).toHaveBeenCalled()
      } finally {
        delete (document.documentElement as unknown as Record<string, unknown>).requestFullscreen
      }
    })

    it('counts down the idle deadline', async () => {
      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'], now: new Date(t(0)) })
      getTemplateJob.mockResolvedValue(job({ state: 'Configuring', idleDeadline: t(5) }))
      renderJob()
      expect(await screen.findByText('Discarded in 5:00 if the console stays closed')).toBeInTheDocument()
      act(() => { vi.advanceTimersByTime(3000) })
      expect(screen.getByText('Discarded in 4:57 if the console stays closed')).toBeInTheDocument()
    })

    it('shows the Defender checklist during Configuring and the refusal next to it', async () => {
      getTemplateJob.mockResolvedValue(job({ state: 'Configuring', message: 'Defender is still on: turn off Tamper Protection.' }))
      renderJob()
      const items = await screen.findAllByRole('listitem')
      const texts = items.map((li) => li.textContent)
      const at = texts.indexOf('Open Windows Security')
      expect(at).toBeGreaterThanOrEqual(0)
      expect(texts.slice(at, at + 5)).toEqual(['Open Windows Security', 'Virus and threat protection → Manage settings', 'Turn off Tamper Protection', 'Turn off Real-time protection', 'Press Seal'])
      expect(screen.getAllByText(/Defender is still on/)).toHaveLength(1)
    })

    it('hides the checklist outside Configuring', async () => {
      getTemplateJob.mockResolvedValue(job({ state: 'Sealing' }))
      renderJob()
      await screen.findByRole('heading', { name: /Edit/ })
      expect(screen.queryByText('Open Windows Security')).toBeNull()
    })

    it('shows the provisioning log inline as text and truncates over 1 MiB with a download', async () => {
      const big = '<script>alert(1)</script>' + 'a'.repeat(1024 * 1024)
      fetchBlob.mockResolvedValue(new Blob([big]))
      downloadFile.mockResolvedValue(undefined)
      getTemplateJob.mockResolvedValue(job({ kind: 'Build', state: 'Provisioning', hasProvisionLog: true }))
      renderJob()
      const pre = await screen.findByText(/<script>alert\(1\)<\/script>/)
      expect(pre.tagName).toBe('PRE')
      expect(document.querySelector('pre script')).toBeNull()
      expect(pre.textContent!.length).toBe(1024 * 1024)
      await userEvent.click(screen.getByRole('button', { name: 'Download the full log' }))
      expect(downloadFile).toHaveBeenCalledWith('/api/admin/template-jobs/job-1/provision-log', 'provision-job-1.log')
    })

    it('a small log has no download', async () => {
      fetchBlob.mockResolvedValue(new Blob(['short']))
      getTemplateJob.mockResolvedValue(job({ kind: 'Build', state: 'Provisioning', hasProvisionLog: true }))
      renderJob()
      await screen.findByText('short')
      expect(screen.queryByRole('button', { name: 'Download the full log' })).toBeNull()
    })

    it('shows a Retry banner when the log fetch fails', async () => {
      fetchBlob.mockRejectedValueOnce(new ApiError(500, { title: 'Log unavailable' })).mockResolvedValue(new Blob(['ok now']))
      getTemplateJob.mockResolvedValue(job({ kind: 'Build', state: 'Provisioning', hasProvisionLog: true }))
      renderJob()
      expect(await screen.findByRole('alert')).toHaveTextContent('Log unavailable')
      await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
      expect(await screen.findByText('ok now')).toBeInTheDocument()
    })
  })

  describe('Build', () => {
    const build = (over: Partial<TemplateJobDto> = {}) => job({ kind: 'Build', baseVersionNumber: null, ...over })
    const stepsOf = () => within(screen.getByRole('list', { name: 'Progress' })).getAllByRole('listitem').map((li) => li.textContent?.trim())

    it('shows the Build steps, the Build heading and no "from v" without a base version', async () => {
      getTemplateJob.mockResolvedValue(build({ state: 'Installing' }))
      renderJob()
      expect(await screen.findByRole('heading', { name: /New template \(build\)/ })).toBeInTheDocument()
      expect(stepsOf()).toEqual(['Queued', 'Preparing', 'Installing', 'Provisioning', 'Configuring', 'Sealing', 'Verifying', 'Published'])
      expect(current()).toHaveTextContent('Installing')
      expect(screen.queryByText(/from v/)).toBeNull()
      hub.push(build({ state: 'Provisioning' }))
      expect(current()).toHaveTextContent('Provisioning')
    })

    it('an Edit still shows from v<n>, and Edit and Refresh steps are unchanged', async () => {
      getTemplateJob.mockResolvedValue(job({ state: 'Preparing', baseVersionNumber: 3 }))
      renderJob()
      expect(await screen.findByText('from v3')).toBeInTheDocument()
      expect(stepsOf()).toEqual(['Queued', 'Preparing', 'Configuring', 'Sealing', 'Verifying', 'Published'])
    })

    it('offers Cancel while installing or provisioning', async () => {
      getTemplateJob.mockResolvedValue(build({ state: 'Installing' }))
      renderJob()
      await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }))
      expect(cancelTemplateJob).toHaveBeenCalledWith('job-1')
    })

    it('shows the provisioning log only when the job has one', async () => {
      fetchBlob.mockResolvedValue(new Blob(['line one']))
      getTemplateJob.mockResolvedValue(build({ state: 'Provisioning', hasProvisionLog: false }))
      renderJob()
      await screen.findByRole('heading', { name: /New template/ })
      expect(screen.queryByRole('heading', { name: 'Provisioning log' })).toBeNull()
      expect(fetchBlob).not.toHaveBeenCalled()
      hub.push(build({ state: 'Configuring', hasProvisionLog: true }))
      expect(await screen.findByText('line one')).toBeInTheDocument()
      expect(fetchBlob).toHaveBeenCalledWith('/api/admin/template-jobs/job-1/provision-log')
    })

    it('Seal requested follows the server; a refusal turns it back, enables Seal, shows the message and remounts the console', async () => {
      getTemplateJob.mockResolvedValue(build({ state: 'Configuring', sealRequested: false }))
      sealTemplateJob.mockResolvedValue(undefined)
      renderJob()
      await userEvent.click(await screen.findByRole('button', { name: 'Seal' }))
      hub.push(build({ state: 'Configuring', sealRequested: true }))
      expect(screen.getByText('Seal requested')).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Seal' })).toBeDisabled()
      expect(getTemplateJobTunnelToken).toHaveBeenCalledTimes(1)

      hub.push(build({ state: 'Configuring', sealRequested: false, message: 'Defender is still on: turn off Tamper Protection and Real-time protection, then seal again.' }))
      await waitFor(() => expect(screen.getByRole('button', { name: 'Seal' })).toBeEnabled())
      expect(screen.queryByText('Seal requested')).toBeNull()
      expect(screen.getByText(/Defender is still on/)).toBeInTheDocument()
      await waitFor(() => expect(getTemplateJobTunnelToken).toHaveBeenCalledTimes(2)) // a fresh token for the remounted console
      expect(guac.state.disconnects).toBe(1)
      expect(screen.getByLabelText('Seal note (optional)')).toBeEnabled()
    })

    it('a push that keeps Seal not requested does not remount the console', async () => {
      getTemplateJob.mockResolvedValue(build({ state: 'Configuring', sealRequested: false }))
      renderJob()
      await screen.findByRole('button', { name: 'Seal' })
      hub.push(build({ state: 'Configuring', sealRequested: false, message: 'still here' }))
      expect(getTemplateJobTunnelToken).toHaveBeenCalledTimes(1)
      expect(guac.state.disconnects).toBe(0)
    })
  })
})
