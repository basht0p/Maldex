import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminTemplateDto, MeDto, TemplateJobDto, TemplateVersionDto } from '../api/types'

const listAdminTemplates = vi.fn()
const setDefaultTemplate = vi.fn()
const setTemplateEnabled = vi.fn()
const rollBackTemplate = vi.fn()
const listTemplateJobs = vi.fn()
const startTemplateEdit = vi.fn()
const startTemplateRefresh = vi.fn()
const publishTemplateVersion = vi.fn()
const discardTemplateVersion = vi.fn()
const deleteTemplate = vi.fn()
const startTemplateBuild = vi.fn()
const listMedia = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  listAdminTemplates: (...a: unknown[]) => listAdminTemplates(...a),
  setDefaultTemplate: (...a: unknown[]) => setDefaultTemplate(...a),
  setTemplateEnabled: (...a: unknown[]) => setTemplateEnabled(...a),
  rollBackTemplate: (...a: unknown[]) => rollBackTemplate(...a),
  listTemplateJobs: (...a: unknown[]) => listTemplateJobs(...a),
  startTemplateEdit: (...a: unknown[]) => startTemplateEdit(...a),
  startTemplateRefresh: (...a: unknown[]) => startTemplateRefresh(...a),
  publishTemplateVersion: (...a: unknown[]) => publishTemplateVersion(...a),
  discardTemplateVersion: (...a: unknown[]) => discardTemplateVersion(...a),
  deleteTemplate: (...a: unknown[]) => deleteTemplate(...a),
  startTemplateBuild: (...a: unknown[]) => startTemplateBuild(...a),
  listMedia: (...a: unknown[]) => listMedia(...a),
}))

import { ApiError } from '../api/client'
import { renderWithKit } from '../test/renderWithKit'
import TemplatesAdminPage from './TemplatesAdminPage'

const admin: MeDto = { id: 'u1', displayName: 'Dev', role: 'Admin', maxUploadBytes: 268_435_456 }

const version = (over: Partial<TemplateVersionDto>): TemplateVersionDto => ({
  id: 'v-id', number: 1, origin: 'Imported', status: 'Ready', role: 'Pending', isCurrent: false, diskPresent: true,
  windowsEdition: null, windowsBuild: null, officePresent: null, agentVersion: null, sealedBy: null,
  sealedAt: '2026-10-24T10:00:00Z', note: null, sizeBytes: null, sourceMedia: [], taskCount: 0, ...over,
})

const v3 = version({
  id: 'g3', number: 3, origin: 'Edit', isCurrent: true, role: 'Current', windowsEdition: 'Windows 11 Pro', windowsBuild: '26300', agentVersion: '1.0.0', sealedBy: 'basht0p',
})
const v2 = version({ id: 'g2', number: 2, origin: 'Refresh', status: 'Ready', role: 'RollbackTarget' })
const v1 = version({ id: 'g1', number: 1, origin: 'IsoBuild', status: 'Deleted', role: 'Deleted', diskPresent: false })

const golden = (over: Partial<AdminTemplateDto> = {}): AdminTemplateDto => ({
  id: 'win11-golden', displayName: 'Windows 11 Golden', description: null, isDefault: false, enabled: true,
  current: v3, versions: [v3, v2, v1], tasksLast30Days: 0, refreshDue: false, activeJobId: null, ...over,
})
const base = (over: Partial<AdminTemplateDto> = {}): AdminTemplateDto => {
  const cur = version({ id: 'b1', number: 1, isCurrent: true, role: 'Current' })
  return { id: 'win11-26h2', displayName: 'Windows 11 26H2', description: null, isDefault: true, enabled: true, current: cur, versions: [cur], tasksLast30Days: 0, refreshDue: false, activeJobId: null, ...over }
}

const job = (over: Partial<TemplateJobDto> = {}): TemplateJobDto => ({
  id: 'job-1', kind: 'Edit', templateId: 'win11-golden', baseVersionNumber: 3, resultVersionNumber: null, network: 'None', state: 'Configuring',
  message: null, failureReason: null, createdAt: '2026-10-24T10:00:00Z', endedAt: null, idleDeadline: null, ...over,
})

function Where() {
  return <p data-testid="where">{useLocation().pathname}</p>
}
const renderPage = (me: MeDto | null = admin) =>
  renderWithKit(
    <Routes>
      <Route path="/admin/templates" element={<TemplatesAdminPage me={me} />} />
      <Route path="*" element={<Where />} />
    </Routes>,
    { route: '/admin/templates' },
  )
/** The text of the open in-app confirmation (title and body). */
const dialogText = async () => (await screen.findByRole('dialog')).textContent ?? ''
const answer = async (label: string) => userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: label }))
let confirmSpy: ReturnType<typeof vi.spyOn>
const region = (name: string) => screen.findByRole('region', { name })

beforeEach(() => {
  vi.clearAllMocks()
  listAdminTemplates.mockResolvedValue([base(), golden()])
  listTemplateJobs.mockResolvedValue([])
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

  it('renders each template with its default badge, enabled toggle, current version and kept previous version', async () => {
    renderPage()
    const def = await region('Windows 11 26H2')
    expect(within(def).getByText('Default')).toBeInTheDocument()
    expect(within(def).getByLabelText('Enabled')).toBeChecked()
    expect(within(def).getByText('No previous version kept')).toBeInTheDocument()

    const g = await region('Windows 11 Golden')
    expect(within(g).queryByText('Default')).toBeNull()
    expect(within(g).getByText('Current: v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)')).toBeInTheDocument()
    expect(within(g).getByText('Previous kept: v2')).toBeInTheDocument()
  })

  it('lists every version with number, origin and status, and warns when the disk is missing', async () => {
    listAdminTemplates.mockResolvedValue([golden({ versions: [v3, version({ ...v2, diskPresent: false }), v1] })])
    renderPage()
    const g = await region('Windows 11 Golden')
    expect(within(g).getByRole('table').parentElement?.className).toMatch(/tableScroll/)
    const rows = within(g).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(3)
    expect(rows[0]).toHaveTextContent(/v3.*Edit.*Ready/)
    expect(rows[1]).toHaveTextContent(/v2.*Refresh.*Ready.*Disk missing/)
    expect(rows[2]).toHaveTextContent(/v1.*IsoBuild.*Deleted/)
    // a Deleted version has no disk by design: no warning there
    expect(within(rows[2]).queryByText('Disk missing')).toBeNull()
    expect(within(g).getAllByText('Disk missing')).toHaveLength(1)
  })

  it('Retired history means no previous version and Roll back is disabled', async () => {
    const retired = version({ id: 'r2', number: 2, status: 'Retired', role: 'Retired' })
    listAdminTemplates.mockResolvedValue([golden({ versions: [v3, retired, v1] })])
    renderPage()
    const g = await region('Windows 11 Golden')
    expect(within(g).getByText('No previous version kept')).toBeInTheDocument()
    expect(within(g).getByRole('button', { name: 'Roll back' })).toBeDisabled()
  })

  it('a Pending version (sealed, never current) is not a rollback target: it is listed apart and Roll back stays disabled', async () => {
    const pending = version({ id: 'p4', number: 4, origin: 'Edit', role: 'Pending' })
    listAdminTemplates.mockResolvedValue([golden({ versions: [pending, v3] })])
    renderPage()
    const g = await region('Windows 11 Golden')
    expect(within(g).getByText('No previous version kept')).toBeInTheDocument()
    expect(within(g).getByText('Not yet published: v4')).toBeInTheDocument()
    expect(within(g).getByRole('button', { name: 'Roll back' })).toBeDisabled()
  })

  it('names exactly the Ready previous version in the confirmation', async () => {
    const v4 = version({ id: 'g4', number: 4, isCurrent: true, origin: 'Edit' })
    const v3r = version({ id: 'r3', number: 3, status: 'Retired', role: 'Retired' })
    const v2r = version({ id: 'g2', number: 2, status: 'Ready', role: 'RollbackTarget' })
    listAdminTemplates.mockResolvedValue([golden({ current: v4, versions: [v4, v3r, v2r] })])
    renderPage()
    const g = await region('Windows 11 Golden')
    expect(within(g).getByText('Previous kept: v2')).toBeInTheDocument()
    await userEvent.click(within(g).getByRole('button', { name: 'Roll back' }))
    const text = await dialogText()
    expect(text).toContain('v2')
    expect(text).not.toContain('v3')
    await answer('Cancel')
  })

  it('says the version rolled back from is retired, never kept', async () => {
    renderPage()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByRole('button', { name: 'Roll back' }))
    const text = await dialogText()
    expect(text).toContain('New tasks will use v2.')
    expect(text).toContain('v3 will be retired')
    expect(text).not.toContain('stays available')
    await answer('Cancel')
  })

  it('takes the rollback target from the server role, not from the highest number, and lists the other kept versions', async () => {
    const c5 = version({ id: 'c5', number: 5, isCurrent: true, role: 'Current' })
    const t2 = version({ id: 't2', number: 2, role: 'RollbackTarget' })
    const p4 = version({ id: 'p4', number: 4, role: 'Previous' })
    listAdminTemplates.mockResolvedValue([golden({ current: c5, versions: [c5, p4, t2] })])
    renderPage()
    const g = await region('Windows 11 Golden')
    expect(within(g).getByText('Previous kept: v2, v4')).toBeInTheDocument()
    await userEvent.click(within(g).getByRole('button', { name: 'Roll back' }))
    const text = await dialogText()
    expect(text).toContain('New tasks will use v2.')
    expect(text).toContain('v5 will be retired')
    await answer('Cancel')
  })

  it('shows a Ready previous version whose disk is missing as kept, with a warning, and disables Roll back', async () => {
    listAdminTemplates.mockResolvedValue([golden({ versions: [v3, version({ ...v2, diskPresent: false })] })])
    renderPage()
    const g = await region('Windows 11 Golden')
    expect(within(g).getByText('Previous kept: v2')).toBeInTheDocument()
    expect(within(g).getAllByText('Disk missing').length).toBeGreaterThan(0)
    const btn = within(g).getByRole('button', { name: 'Roll back' })
    expect(btn).toBeDisabled()
    expect(btn).toHaveAttribute('title', expect.stringMatching(/disk is missing/i))
  })

  it.each([
    [true, 'Office ✓'],
    [false, 'Office ✗'],
    [null, 'Office unknown'],
  ])('shows Office presence (%s) in the current-version summary', async (officePresent, text) => {
    const cur = version({ ...v3, officePresent })
    listAdminTemplates.mockResolvedValue([golden({ current: cur, versions: [cur] })])
    renderPage()
    const g = await region('Windows 11 Golden')
    expect(within(g).getByText(text)).toBeInTheDocument()
  })

  it('does not warn when every disk is present', async () => {
    renderPage()
    await region('Windows 11 Golden')
    expect(screen.queryByText('Disk missing')).toBeNull()
  })

  it('rolls back only after the admin confirms', async () => {
    rollBackTemplate.mockResolvedValue(golden())
    renderPage()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByRole('button', { name: 'Roll back' }))
    expect(await screen.findByRole('dialog', { name: 'Roll back Windows 11 Golden to v2?' })).toBeInTheDocument()
    expect(rollBackTemplate).not.toHaveBeenCalled()
    await answer('Roll back')
    expect(rollBackTemplate).toHaveBeenCalledWith('win11-golden')
    await waitFor(() => expect(listAdminTemplates).toHaveBeenCalledTimes(2))
  })

  it('does not roll back when the confirmation is declined', async () => {
    renderPage()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByRole('button', { name: 'Roll back' }))
    await answer('Cancel')
    expect(rollBackTemplate).not.toHaveBeenCalled()
  })

  it('disables Roll back when there is no previous version to return to', async () => {
    renderPage()
    const def = await region('Windows 11 26H2')
    expect(within(def).getByRole('button', { name: 'Roll back' })).toBeDisabled()
  })

  it('shows the 409 message inline when disabling the default template', async () => {
    setTemplateEnabled.mockRejectedValue(new ApiError(409, { title: 'The default template cannot be disabled.', status: 409 }))
    renderPage()
    const def = await region('Windows 11 26H2')
    await userEvent.click(within(def).getByLabelText('Enabled'))
    expect(setTemplateEnabled).toHaveBeenCalledWith('win11-26h2', false)
    expect(await screen.findByText('The default template cannot be disabled.')).toBeInTheDocument()
    expect(within(def).getByLabelText('Enabled')).toBeChecked()
  })

  it('toggles Enabled and reloads', async () => {
    setTemplateEnabled.mockResolvedValue(undefined)
    renderPage()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByLabelText('Enabled'))
    expect(setTemplateEnabled).toHaveBeenCalledWith('win11-golden', false)
    await waitFor(() => expect(listAdminTemplates).toHaveBeenCalledTimes(2))
  })

  it('sets a template as default (disabled on the current default) and reloads', async () => {
    setDefaultTemplate.mockResolvedValue(undefined)
    renderPage()
    const def = await region('Windows 11 26H2')
    expect(within(def).getByRole('button', { name: 'Set as default' })).toBeDisabled()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByRole('button', { name: 'Set as default' }))
    expect(setDefaultTemplate).toHaveBeenCalledWith('win11-golden')
    await waitFor(() => expect(listAdminTemplates).toHaveBeenCalledTimes(2))
  })

  it('shows a load error', async () => {
    listAdminTemplates.mockRejectedValue(new ApiError(500, { title: 'Boom' }))
    renderPage()
    expect(await screen.findByRole('alert')).toHaveTextContent('Boom')
    listAdminTemplates.mockResolvedValue([base(), golden()])
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await region('Windows 11 Golden')).toBeInTheDocument()
  })

  describe('Edit, Refresh and jobs', () => {
    it('disables Edit and Refresh components on a template with no current version, saying why', async () => {
      listAdminTemplates.mockResolvedValue([golden({ id: 'win11-new', displayName: 'Windows 11 New', current: null, versions: [] })])
      renderPage()
      const g = await region('Windows 11 New')
      for (const name of ['Edit', 'Refresh components']) {
        const b = within(g).getByRole('button', { name })
        expect(b).toBeDisabled()
        expect(b).toHaveAttribute('title', 'no version yet')
        expect(b).toHaveAccessibleDescription('no version yet')
      }
    })

    it('Edit sends the chosen network and publish flag, then opens the job', async () => {
      startTemplateEdit.mockResolvedValue(job({ id: 'job-9' }))
      renderPage()
      const g = await region('Windows 11 Golden')
      await userEvent.click(within(g).getByRole('button', { name: 'Edit' }))
      const dlg = within(g).getByRole('dialog')
      expect(within(dlg).getByLabelText('None')).toBeChecked()
      expect(within(dlg).getByLabelText('Publish when done')).toBeChecked()
      await userEvent.click(within(dlg).getByLabelText('Direct: internet on'))
      await userEvent.click(within(dlg).getByLabelText('Publish when done'))
      await userEvent.click(within(dlg).getByRole('button', { name: 'Start edit' }))
      expect(startTemplateEdit).toHaveBeenCalledWith('win11-golden', 'Direct', false)
      expect(await screen.findByTestId('where')).toHaveTextContent('/admin/template-jobs/job-9')
    })

    it('moves focus into the dialog, traps Tab, closes on Escape and returns focus to the trigger', async () => {
      renderPage()
      const g = await region('Windows 11 Golden')
      const trigger = within(g).getByRole('button', { name: 'Edit' })
      await userEvent.click(trigger)
      const dlg = within(g).getByRole('dialog')
      await waitFor(() => expect(dlg).toContainElement(document.activeElement as HTMLElement))
      for (let i = 0; i < 12; i++) {
        await userEvent.tab()
        expect(dlg).toContainElement(document.activeElement as HTMLElement)
      }
      for (let i = 0; i < 12; i++) {
        await userEvent.tab({ shift: true })
        expect(dlg).toContainElement(document.activeElement as HTMLElement)
      }
      await userEvent.keyboard('{Escape}')
      expect(within(g).queryByRole('dialog')).toBeNull()
      expect(trigger).toHaveFocus()
    })

    it('Cancel also returns focus to the trigger', async () => {
      renderPage()
      const g = await region('Windows 11 Golden')
      const trigger = within(g).getByRole('button', { name: 'Refresh components' })
      await userEvent.click(trigger)
      await userEvent.click(within(g).getByRole('button', { name: 'Cancel' }))
      expect(trigger).toHaveFocus()
    })

    it('when the active job ends, the poll refreshes the versions and re-enables Edit and Refresh', async () => {
      const pending = version({ id: 'p4', number: 4, origin: 'Edit', role: 'Pending' })
      listTemplateJobs.mockResolvedValue([job({ state: 'Sealing' })])
      renderPage()
      const g = await region('Windows 11 Golden')
      await waitFor(() => expect(within(g).getByRole('button', { name: 'Edit' })).toBeDisabled())
      const loads = listAdminTemplates.mock.calls.length
      listTemplateJobs.mockResolvedValue([job({ state: 'Sealed', resultVersionNumber: 4 })])
      listAdminTemplates.mockResolvedValue([base(), golden({ versions: [pending, v3, v2, v1] })])
      await waitFor(() => expect(within(g).getByRole('button', { name: 'Edit' })).toBeEnabled(), { timeout: 8000 })
      expect(listAdminTemplates.mock.calls.length).toBeGreaterThan(loads)
      expect(within(g).getByRole('button', { name: 'Publish v4' })).toBeInTheDocument()
      expect(within(g).getByRole('button', { name: 'Refresh components' })).toBeEnabled()
    }, 15000)

    it('Edit defaults to network None and publishing', async () => {
      startTemplateEdit.mockResolvedValue(job())
      renderPage()
      const g = await region('Windows 11 Golden')
      await userEvent.click(within(g).getByRole('button', { name: 'Edit' }))
      await userEvent.click(within(g).getByRole('button', { name: 'Start edit' }))
      expect(startTemplateEdit).toHaveBeenCalledWith('win11-golden', 'None', true)
    })

    it('Refresh components asks for confirmation and sends only the publish flag', async () => {
      startTemplateRefresh.mockResolvedValue(job({ kind: 'Refresh', id: 'job-r' }))
      renderPage()
      const g = await region('Windows 11 Golden')
      await userEvent.click(within(g).getByRole('button', { name: 'Refresh components' }))
      const dlg = within(g).getByRole('dialog')
      expect(within(dlg).queryByLabelText('None')).toBeNull()
      expect(startTemplateRefresh).not.toHaveBeenCalled()
      await userEvent.click(within(dlg).getByRole('button', { name: 'Start refresh' }))
      expect(startTemplateRefresh).toHaveBeenCalledWith('win11-golden', true)
      expect(await screen.findByTestId('where')).toHaveTextContent('/admin/template-jobs/job-r')
    })

    it('shows the server 409 reason inside the dialog and stays on the page', async () => {
      startTemplateEdit.mockRejectedValue(new ApiError(409, { title: 'The base disk for v3 is missing.', status: 409 }))
      renderPage()
      const g = await region('Windows 11 Golden')
      await userEvent.click(within(g).getByRole('button', { name: 'Edit' }))
      await userEvent.click(within(g).getByRole('button', { name: 'Start edit' }))
      expect(await within(g).findByRole('alert')).toHaveTextContent('The base disk for v3 is missing.')
      expect(screen.queryByTestId('where')).toBeNull()
    })

    it('disables Edit and Refresh with the reason while a job is active, and links to it', async () => {
      listTemplateJobs.mockImplementation(async (id: string) => (id === 'win11-golden' ? [job({ state: 'Preparing' })] : []))
      renderPage()
      const g = await region('Windows 11 Golden')
      await waitFor(() => expect(within(g).getByRole('button', { name: 'Edit' })).toBeDisabled())
      expect(within(g).getByRole('button', { name: 'Edit' })).toHaveAttribute('title', expect.stringMatching(/already preparing/i))
      expect(within(g).getByRole('button', { name: 'Refresh components' })).toBeDisabled()
      expect(within(g).getByRole('link', { name: 'Open the job' })).toHaveAttribute('href', '/admin/template-jobs/job-1')
      const other = await region('Windows 11 26H2')
      expect(within(other).getByRole('button', { name: 'Edit' })).toBeEnabled()
    })

    it('a finished job does not block Edit and Refresh; the Jobs list links each job', async () => {
      listTemplateJobs.mockResolvedValue([job({ id: 'j2', state: 'Failed', failureReason: 'Verify failed.' }), job({ id: 'j1', state: 'Published', resultVersionNumber: 4 })])
      renderPage()
      const g = await region('Windows 11 Golden')
      const jobs = await within(g).findByRole('table', { name: 'Jobs' })
      expect(within(jobs).getByText('Failed: Verify failed.')).toBeInTheDocument()
      const links = within(jobs).getAllByRole('link')
      expect(links[0]).toHaveAttribute('href', '/admin/template-jobs/j2')
      expect(within(g).getByRole('button', { name: 'Edit' })).toBeEnabled()
    })

    it('a Pending version shows Publish and Discard; Publish calls the route and reloads', async () => {
      const pending = version({ id: 'p4', number: 4, origin: 'Edit', role: 'Pending' })
      listAdminTemplates.mockResolvedValue([golden({ versions: [pending, v3] })])
      publishTemplateVersion.mockResolvedValue(golden())
      renderPage()
      const g = await region('Windows 11 Golden')
      await userEvent.click(within(g).getByRole('button', { name: 'Publish v4' }))
      expect(publishTemplateVersion).toHaveBeenCalledWith('win11-golden', 'p4')
      await waitFor(() => expect(listAdminTemplates).toHaveBeenCalledTimes(2))
      expect(within(g).queryByRole('button', { name: 'Publish v3' })).toBeNull()
    })

    it('Discard asks first and only then calls the route', async () => {
      const pending = version({ id: 'p4', number: 4, role: 'Pending' })
      listAdminTemplates.mockResolvedValue([golden({ versions: [pending, v3] })])
      discardTemplateVersion.mockResolvedValue(undefined)
      renderPage()
      const g = await region('Windows 11 Golden')
      await userEvent.click(within(g).getByRole('button', { name: 'Discard v4' }))
      expect(await screen.findByRole('dialog', { name: 'Discard v4 of Windows 11 Golden?' })).toHaveTextContent('It is never published and its disk is deleted.')
      await answer('Cancel')
      expect(discardTemplateVersion).not.toHaveBeenCalled()
      await userEvent.click(within(g).getByRole('button', { name: 'Discard v4' }))
      await answer('Discard')
      expect(discardTemplateVersion).toHaveBeenCalledWith('win11-golden', 'p4')
      expect(await screen.findByText('v4 discarded')).toBeInTheDocument()
    })

    it('shows a 409 from Publish inline', async () => {
      const pending = version({ id: 'p4', number: 4, role: 'Pending' })
      listAdminTemplates.mockResolvedValue([golden({ versions: [pending, v3] })])
      publishTemplateVersion.mockRejectedValue(new ApiError(409, { title: 'Only a held version can be published.', status: 409 }))
      renderPage()
      const g = await region('Windows 11 Golden')
      await userEvent.click(within(g).getByRole('button', { name: 'Publish v4' }))
      expect(await screen.findByText('Only a held version can be published.')).toBeInTheDocument()
    })

    it('shows no Publish or Discard on versions that are not Pending', async () => {
      renderPage()
      const g = await region('Windows 11 Golden')
      expect(within(g).queryByRole('button', { name: /^Publish v/ })).toBeNull()
      expect(within(g).queryByRole('button', { name: /^Discard v/ })).toBeNull()
    })
  })

  describe('Media, New template and Delete', () => {
    const empty = (over: Partial<AdminTemplateDto> = {}): AdminTemplateDto => ({
      id: 'win11-new', displayName: 'Windows 11 New', description: null, isDefault: false, enabled: true, current: null, versions: [], tasksLast30Days: 0, refreshDue: false, activeJobId: null, ...over,
    })
    const iso = {
      id: 'os1', kind: 'OsInstaller', fileName: 'win11.iso', size: 10, receivedBytes: 10, sha256: 'ab', source: 'Upload', status: 'Ready',
      images: [{ index: 6, name: 'Windows 11 Pro', build: '26300', supported: true }], preferredImageIndex: 6, addedBy: 'dev', addedAt: '2026-10-25T10:00:00Z', usedBy: [],
    }

    it('has Templates and Media tabs; the Media tab shows the library', async () => {
      renderPage()
      await region('Windows 11 Golden')
      expect(screen.getByRole('tab', { name: 'Templates' })).toHaveAttribute('aria-selected', 'true')
      await userEvent.click(screen.getByRole('tab', { name: 'Media' }))
      expect(screen.getByRole('tab', { name: 'Media' })).toHaveAttribute('aria-selected', 'true')
      expect(await screen.findByRole('region', { name: 'Media library' })).toBeInTheDocument()
      expect(listMedia).toHaveBeenCalled()
      expect(screen.queryByRole('region', { name: 'Windows 11 Golden' })).toBeNull()
      await userEvent.click(screen.getByRole('tab', { name: 'Templates' }))
      expect(await region('Windows 11 Golden')).toBeInTheDocument()
    })

    it('New template opens the Build dialog with the same focus handling and returns focus to its button', async () => {
      renderPage()
      await region('Windows 11 Golden')
      const trigger = screen.getByRole('button', { name: 'New template' })
      await userEvent.click(trigger)
      const dlg = await screen.findByRole('dialog', { name: 'New template' })
      await waitFor(() => expect(dlg).toContainElement(document.activeElement as HTMLElement))
      for (let i = 0; i < 12; i++) {
        await userEvent.tab()
        expect(dlg).toContainElement(document.activeElement as HTMLElement)
      }
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

    it('a template without a version says so and, when it can be deleted, offers Delete', async () => {
      listAdminTemplates.mockResolvedValue([base(), empty()])
      renderPage()
      const e = await region('Windows 11 New')
      expect(within(e).getByText('No version yet')).toBeInTheDocument()
      expect(await within(e).findByRole('button', { name: 'Delete' })).toBeEnabled()
    })

    it('offers no Delete when the template has versions, is the default, or has an active job', async () => {
      const emptyDefault = empty({ id: 'win11-def', displayName: 'Empty Default', isDefault: true })
      const busy = empty({ id: 'win11-busy', displayName: 'Building' })
      listAdminTemplates.mockResolvedValue([base(), golden(), emptyDefault, busy])
      listTemplateJobs.mockImplementation(async (id: string) => (id === 'win11-busy' ? [job({ kind: 'Build', state: 'Installing', baseVersionNumber: null })] : []))
      renderPage()
      const b = await region('Building')
      await waitFor(() => expect(within(b).getByRole('link', { name: 'Open the job' })).toBeInTheDocument())
      for (const name of ['Windows 11 26H2', 'Windows 11 Golden', 'Empty Default', 'Building']) {
        expect(within(await region(name)).queryByRole('button', { name: 'Delete' })).toBeNull()
      }
    })

    it('offers no Delete until the jobs are known (an active Build must not look absent)', async () => {
      listAdminTemplates.mockResolvedValue([base(), empty()])
      listTemplateJobs.mockImplementation(() => new Promise(() => undefined))
      renderPage()
      const e = await region('Windows 11 New')
      expect(within(e).getByText('No version yet')).toBeInTheDocument()
      expect(within(e).queryByRole('button', { name: 'Delete' })).toBeNull()
    })

    it('Delete asks first, then deletes and reloads', async () => {
      listAdminTemplates.mockResolvedValue([base(), empty()])
      deleteTemplate.mockResolvedValue(undefined)
      renderPage()
      const e = await region('Windows 11 New')
      const del = await within(e).findByRole('button', { name: 'Delete' })
      await userEvent.click(del)
      expect(await screen.findByRole('dialog', { name: 'Delete Windows 11 New?' })).toHaveTextContent('It has no versions; the template entry is removed.')
      await answer('Cancel')
      expect(deleteTemplate).not.toHaveBeenCalled()
      await userEvent.click(del)
      await answer('Delete')
      expect(deleteTemplate).toHaveBeenCalledWith('win11-new')
      await waitFor(() => expect(listAdminTemplates).toHaveBeenCalledTimes(2))
    })

    it('shows the 409 reason from Delete inline', async () => {
      listAdminTemplates.mockResolvedValue([base(), empty()])
      deleteTemplate.mockRejectedValue(new ApiError(409, { title: 'A job is active for this template.', status: 409 }))
      renderPage()
      const e = await region('Windows 11 New')
      await userEvent.click(await within(e).findByRole('button', { name: 'Delete' }))
      await answer('Delete')
      expect(await screen.findByText('A job is active for this template.')).toBeInTheDocument()
    })

    it('the jobs table shows a Build as base "new"', async () => {
      listTemplateJobs.mockResolvedValue([job({ id: 'jb', kind: 'Build', baseVersionNumber: null, resultVersionNumber: 1, state: 'Published' })])
      renderPage()
      const g = await region('Windows 11 Golden')
      const jobs = await within(g).findByRole('table', { name: 'Jobs' })
      expect(within(jobs).getByRole('link', { name: 'Build' })).toBeInTheDocument()
      expect(within(jobs).getAllByRole('row')[1]).toHaveTextContent(/new\s*v1/)
    })

    it('a failed reload after a job ends keeps the last versions and offers a retry', async () => {
      listTemplateJobs.mockResolvedValue([job({ state: 'Sealing' })])
      renderPage()
      const g = await region('Windows 11 Golden')
      await waitFor(() => expect(within(g).getByRole('button', { name: 'Edit' })).toBeDisabled())
      listTemplateJobs.mockResolvedValue([job({ state: 'Published', resultVersionNumber: 4 })])
      listAdminTemplates.mockRejectedValue(new ApiError(500, { title: 'Boom' }))
      expect(await screen.findByText(/showing the last loaded versions/i, undefined, { timeout: 8000 })).toHaveTextContent('Boom')
      expect(within(g).getByText('Current: v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)')).toBeInTheDocument()
      listAdminTemplates.mockResolvedValue([base(), golden()])
      await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
      await waitFor(() => expect(screen.queryByText(/showing the last loaded versions/i)).toBeNull())
    }, 15000)
  })
})
