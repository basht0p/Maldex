import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminTemplateDto, MeDto, TemplateJobDto, TemplateVersionDto } from '../api/types'

const listAdminTemplates = vi.fn()
const setDefaultTemplate = vi.fn()
const startTemplateEdit = vi.fn()
const startTemplateRefresh = vi.fn()
const startTemplateBuild = vi.fn()
const listMedia = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  listAdminTemplates: (...a: unknown[]) => listAdminTemplates(...a),
  setDefaultTemplate: (...a: unknown[]) => setDefaultTemplate(...a),
  startTemplateEdit: (...a: unknown[]) => startTemplateEdit(...a),
  startTemplateRefresh: (...a: unknown[]) => startTemplateRefresh(...a),
  startTemplateBuild: (...a: unknown[]) => startTemplateBuild(...a),
  listMedia: (...a: unknown[]) => listMedia(...a),
}))

import { ApiError } from '../api/client'
import { renderWithKit } from '../test/renderWithKit'
import TemplatesAdminPage from './TemplatesAdminPage'

const admin: MeDto = { id: 'u1', displayName: 'Dev', role: 'Admin', kind: 'Local', totpEnabled: false, isBreakGlass: false, roleManagedByIdp: false, maxUploadBytes: 268_435_456 }

const version = (over: Partial<TemplateVersionDto>): TemplateVersionDto => ({
  id: 'v-id', number: 1, origin: 'Imported', status: 'Ready', role: 'Pending', isCurrent: false, diskPresent: true,
  windowsEdition: null, windowsBuild: null, officePresent: null, agentVersion: null, sealedBy: null,
  sealedAt: '2026-10-24T10:00:00Z', note: null, sizeBytes: null, sourceMedia: [], taskCount: 0, ...over,
})

const v3 = version({
  id: 'g3', number: 3, origin: 'Edit', isCurrent: true, role: 'Current', windowsEdition: 'Windows 11 Pro', windowsBuild: '26100.x', officePresent: false, agentVersion: '1.4.2', sealedBy: 'basht0p',
})
const v2 = version({ id: 'g2', number: 2, origin: 'Refresh', status: 'Ready', role: 'RollbackTarget' })

const golden = (over: Partial<AdminTemplateDto> = {}): AdminTemplateDto => ({
  id: 'win11-golden', displayName: 'Windows 11 Golden', description: null, isDefault: false, enabled: true,
  current: v3, versions: [v3, v2], tasksLast30Days: 12, refreshDue: false, activeJobId: null, ...over,
})
const base = (over: Partial<AdminTemplateDto> = {}): AdminTemplateDto => {
  const cur = version({ id: 'b1', number: 1, isCurrent: true, role: 'Current' })
  return { id: 'win11-26h2', displayName: 'Windows 11 26H2', description: null, isDefault: true, enabled: true, current: cur, versions: [cur], tasksLast30Days: 1, refreshDue: false, activeJobId: null, ...over }
}
const empty = (over: Partial<AdminTemplateDto> = {}): AdminTemplateDto => ({
  id: 'win11-new', displayName: 'Windows 11 New', description: null, isDefault: false, enabled: true, current: null, versions: [], tasksLast30Days: 0, refreshDue: false, activeJobId: null, ...over,
})

const job = (over: Partial<TemplateJobDto> = {}): TemplateJobDto => ({
  id: 'job-1', kind: 'Edit', templateId: 'win11-golden', baseVersionNumber: 3, resultVersionNumber: null, network: 'None', state: 'Configuring',
  message: null, failureReason: null, createdAt: '2026-10-24T10:00:00Z', endedAt: null, idleDeadline: null, ...over,
})

function Where() {
  const l = useLocation()
  return <p data-testid="where">{l.pathname}{l.search}</p>
}
const renderPage = (me: MeDto | null = admin, route = '/admin/templates') =>
  renderWithKit(
    <Routes>
      <Route path="/admin/templates" element={<TemplatesAdminPage me={me} />} />
      <Route path="*" element={<Where />} />
    </Routes>,
    { route },
  )
const tick = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })
const answer = async (label: string) => userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: label }))
const region = (name: string) => screen.findByRole('region', { name })
let confirmSpy: ReturnType<typeof vi.spyOn>

beforeEach(() => {
  vi.clearAllMocks()
  listAdminTemplates.mockResolvedValue([base(), golden()])
  listMedia.mockResolvedValue([])
  confirmSpy = vi.spyOn(window, 'confirm')
})
afterEach(() => {
  expect(confirmSpy).not.toHaveBeenCalled() // confirmations are in-app dialogs, never window.confirm
  vi.restoreAllMocks()
})

describe('TemplatesAdminPage', () => {
  it('tells non-admins that administrator access is required', async () => {
    renderPage({ ...admin, role: 'Analyst' })
    expect(await screen.findByRole('alert')).toHaveTextContent(/administrator access is required/i)
    expect(listAdminTemplates).not.toHaveBeenCalled()
  })

  it('shows a card per template with current version, refresh due and usage', async () => {
    listAdminTemplates.mockResolvedValue([base({ enabled: false }), golden({ refreshDue: true })])
    renderPage()
    const g = await region('Windows 11 Golden')
    expect(within(g).getByText('v3 · Windows 11 Pro 26100.x · Office: no · agent 1.4.2')).toBeInTheDocument()
    expect(g.textContent).toMatch(/sealed /)
    expect(within(g).getByText('sealed', { exact: false }).querySelector('time')).not.toBeNull()
    expect(within(g).getByText('Refresh due')).toBeInTheDocument()
    expect(within(g).getByText('12 tasks in the last 30 days')).toBeInTheDocument()
    expect(within(g).queryByText('Default')).toBeNull()
    expect(within(g).queryByText('Disabled')).toBeNull()
    const def = await region('Windows 11 26H2')
    expect(within(def).getByText('Default')).toBeInTheDocument()
    expect(within(def).getByText('Disabled')).toBeInTheDocument()
    expect(within(def).queryByText('Refresh due')).toBeNull()
    expect(within(def).getByText('1 task in the last 30 days')).toBeInTheDocument()
    expect(screen.queryByRole('table')).toBeNull() // versions and jobs live on the detail page
  })

  it('links the name to the detail page and the active job badge to the job', async () => {
    listAdminTemplates.mockResolvedValue([golden({ activeJobId: 'job-7' })])
    renderPage()
    const g = await region('Windows 11 Golden')
    expect(within(g).getByRole('link', { name: 'Windows 11 Golden' })).toHaveAttribute('href', '/admin/templates/win11-golden')
    expect(within(g).getByRole('link', { name: 'Active job' })).toHaveAttribute('href', '/admin/template-jobs/job-7')
    expect(within(g).getByRole('link', { name: 'Details' })).toHaveAttribute('href', '/admin/templates/win11-golden')
  })

  it('shows no Active job badge when there is none', async () => {
    renderPage()
    await region('Windows 11 Golden')
    expect(screen.queryByText('Active job')).toBeNull()
  })

  it('renders admin-entered names as safe text', async () => {
    listAdminTemplates.mockResolvedValue([golden({ displayName: 'Evil‮gnp.exe', description: 'a‮b' })])
    renderPage()
    const g = await region('Evil\\u202Egnp.exe') // the override character is shown literally, not applied
    expect(g.textContent).not.toContain('‮')
    expect(within(g).getByText('a\\u202Eb')).toBeInTheDocument()
  })

  it('redirects ?tab=media to /admin/media', async () => {
    renderPage(admin, '/admin/templates?tab=media')
    expect(await screen.findByTestId('where')).toHaveTextContent('/admin/media')
    expect(screen.getByTestId('where')).not.toHaveTextContent('tab=media')
    expect(screen.queryByRole('tab', { name: 'Media' })).toBeNull()
  })

  it('has no tabs and does not render the media library', async () => {
    renderPage()
    await region('Windows 11 Golden')
    expect(screen.queryByRole('tablist')).toBeNull()
    expect(listMedia).not.toHaveBeenCalled()
  })

  it('renders a template with no versions without crashing and disables Edit/Refresh with a reason', async () => {
    listAdminTemplates.mockResolvedValue([empty()])
    renderPage()
    const e = await region('Windows 11 New')
    expect(within(e).getByText('No version yet')).toBeInTheDocument()
    for (const name of ['Edit', 'Refresh']) {
      const b = within(e).getByRole('button', { name })
      expect(b).toBeDisabled()
      expect(b).toHaveAttribute('title', 'no version yet')
      expect(b).toHaveAccessibleDescription('no version yet')
    }
  })

  it('disables Edit and Refresh while a job is active, with the reason', async () => {
    listAdminTemplates.mockResolvedValue([golden({ activeJobId: 'job-7' })])
    renderPage()
    const g = await region('Windows 11 Golden')
    for (const name of ['Edit', 'Refresh']) {
      const b = within(g).getByRole('button', { name })
      expect(b).toBeDisabled()
      expect(b).toHaveAttribute('title', 'A job is already running for this template.')
    }
  })

  it('Set default asks for confirmation and toasts', async () => {
    setDefaultTemplate.mockResolvedValue(undefined)
    renderPage()
    const def = await region('Windows 11 26H2')
    expect(within(def).getByRole('button', { name: 'Set default' })).toBeDisabled()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByRole('button', { name: 'Set default' }))
    expect(await screen.findByRole('dialog', { name: 'Make Windows 11 Golden the default?' })).toBeInTheDocument()
    expect(setDefaultTemplate).not.toHaveBeenCalled()
    await answer('Set as default')
    expect(setDefaultTemplate).toHaveBeenCalledWith('win11-golden')
    expect(await screen.findByText('Windows 11 Golden is the default')).toBeInTheDocument()
    await waitFor(() => expect(listAdminTemplates).toHaveBeenCalledTimes(2))
  })

  it('does not set the default when the confirmation is declined', async () => {
    renderPage()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByRole('button', { name: 'Set default' }))
    await answer('Cancel')
    expect(setDefaultTemplate).not.toHaveBeenCalled()
  })

  it('shows a 409 from Set default as a toast', async () => {
    setDefaultTemplate.mockRejectedValue(new ApiError(409, { title: 'A disabled template cannot be the default.', status: 409 }))
    renderPage()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByRole('button', { name: 'Set default' }))
    await answer('Set as default')
    expect(await screen.findByText('A disabled template cannot be the default.')).toBeInTheDocument()
  })

  it('shows a load error and retries', async () => {
    listAdminTemplates.mockRejectedValue(new ApiError(500, { title: 'Boom' }))
    renderPage()
    expect(await screen.findByRole('alert')).toHaveTextContent('Boom')
    listAdminTemplates.mockResolvedValue([base(), golden()])
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await region('Windows 11 Golden')).toBeInTheDocument()
  })

  describe('Edit and Refresh', () => {
    it('Edit sends the chosen network and publish flag, then opens the job', async () => {
      startTemplateEdit.mockResolvedValue(job({ id: 'job-9' }))
      renderPage()
      const g = await region('Windows 11 Golden')
      await userEvent.click(within(g).getByRole('button', { name: 'Edit' }))
      const dlg = within(g).getByRole('dialog')
      expect(within(dlg).getByLabelText('None')).toBeChecked()
      await userEvent.click(within(dlg).getByLabelText('Direct: internet on'))
      await userEvent.click(within(dlg).getByLabelText('Publish when done'))
      await userEvent.click(within(dlg).getByRole('button', { name: 'Start edit' }))
      expect(startTemplateEdit).toHaveBeenCalledWith('win11-golden', 'Direct', false)
      expect(await screen.findByTestId('where')).toHaveTextContent('/admin/template-jobs/job-9')
    })

    it('Refresh sends only the publish flag, then opens the job', async () => {
      startTemplateRefresh.mockResolvedValue(job({ kind: 'Refresh', id: 'job-r' }))
      renderPage()
      const g = await region('Windows 11 Golden')
      await userEvent.click(within(g).getByRole('button', { name: 'Refresh' }))
      const dlg = within(g).getByRole('dialog')
      expect(within(dlg).queryByLabelText('None')).toBeNull()
      await userEvent.click(within(dlg).getByRole('button', { name: 'Start refresh' }))
      expect(startTemplateRefresh).toHaveBeenCalledWith('win11-golden', true)
      expect(await screen.findByTestId('where')).toHaveTextContent('/admin/template-jobs/job-r')
    })

    it('shows the server 409 reason inside the dialog and returns focus to the trigger on Escape', async () => {
      startTemplateEdit.mockRejectedValue(new ApiError(409, { title: 'The base disk for v3 is missing.', status: 409 }))
      renderPage()
      const g = await region('Windows 11 Golden')
      const trigger = within(g).getByRole('button', { name: 'Edit' })
      await userEvent.click(trigger)
      await userEvent.click(within(g).getByRole('button', { name: 'Start edit' }))
      expect(await within(g).findByRole('alert')).toHaveTextContent('The base disk for v3 is missing.')
      await userEvent.keyboard('{Escape}')
      expect(within(g).queryByRole('dialog')).toBeNull()
      expect(trigger).toHaveFocus()
    })
  })

  describe('New template', () => {
    const iso = {
      id: 'os1', kind: 'OsInstaller', fileName: 'win11.iso', size: 10, receivedBytes: 10, sha256: 'ab', source: 'Upload', status: 'Ready',
      images: [{ index: 6, name: 'Windows 11 Pro', build: '26300', supported: true }], preferredImageIndex: 6, addedBy: 'dev', addedAt: '2026-10-25T10:00:00Z', usedBy: [],
    }

    it('opens the Build dialog with focus handling and returns focus to its button', async () => {
      renderPage()
      await region('Windows 11 Golden')
      const trigger = screen.getByRole('button', { name: 'New template' })
      await userEvent.click(trigger)
      const dlg = await screen.findByRole('dialog', { name: 'New template' })
      await waitFor(() => expect(dlg).toContainElement(document.activeElement as HTMLElement))
      await userEvent.keyboard('{Escape}')
      expect(screen.queryByRole('dialog')).toBeNull()
      expect(trigger).toHaveFocus()
    })

    it('starting the build opens its job page', async () => {
      listMedia.mockResolvedValue([iso])
      startTemplateBuild.mockResolvedValue(job({ id: 'job-b', kind: 'Build', baseVersionNumber: null }))
      renderPage()
      await region('Windows 11 Golden')
      await userEvent.click(screen.getByRole('button', { name: 'New template' }))
      const dlg = await screen.findByRole('dialog', { name: 'New template' })
      await within(dlg).findByRole('option', { name: 'win11.iso' })
      await userEvent.type(within(dlg).getByLabelText('Template id'), 'win11-new')
      await userEvent.type(within(dlg).getByLabelText('Display name'), 'Windows 11 New')
      await userEvent.click(within(dlg).getByRole('button', { name: 'Start build' }))
      expect(startTemplateBuild).toHaveBeenCalledWith(expect.objectContaining({ templateId: 'win11-new', osMediaId: 'os1' }))
      expect(await screen.findByTestId('where')).toHaveTextContent('/admin/template-jobs/job-b')
    })
  })

  describe('reload while a job runs', () => {
    afterEach(() => {
      vi.useRealTimers()
      vi.restoreAllMocks()
    })

    it('reloads every 5 s, skipping ticks while the tab is hidden or a request is in flight', async () => {
      vi.useFakeTimers()
      listAdminTemplates.mockResolvedValue([golden({ activeJobId: 'job-7' })])
      renderPage()
      await tick(0)
      expect(listAdminTemplates).toHaveBeenCalledTimes(1)
      await tick(5000)
      expect(listAdminTemplates).toHaveBeenCalledTimes(2)

      const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true)
      await tick(5000)
      expect(listAdminTemplates).toHaveBeenCalledTimes(2)
      hidden.mockReturnValue(false)

      let release: (v: AdminTemplateDto[]) => void = () => undefined
      listAdminTemplates.mockImplementation(() => new Promise<AdminTemplateDto[]>((r) => { release = r }))
      await tick(5000)
      expect(listAdminTemplates).toHaveBeenCalledTimes(3)
      await tick(5000) // still in flight: skipped
      expect(listAdminTemplates).toHaveBeenCalledTimes(3)
      release([golden({ activeJobId: 'job-7' })])
      await tick(5000)
      expect(listAdminTemplates).toHaveBeenCalledTimes(4)
    })
  })
})
