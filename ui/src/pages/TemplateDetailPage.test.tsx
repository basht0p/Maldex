import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Link, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminTemplateDto, MeDto, TemplateDetailDto, TemplateJobDto, TemplateVersionDto } from '../api/types'

const getAdminTemplate = vi.fn()
const setDefaultTemplate = vi.fn()
const setTemplateEnabled = vi.fn()
const rollBackTemplate = vi.fn()
const deleteTemplate = vi.fn()
const publishTemplateVersion = vi.fn()
const discardTemplateVersion = vi.fn()
const startTemplateEdit = vi.fn()
const startTemplateRefresh = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  getAdminTemplate: (...a: unknown[]) => getAdminTemplate(...a),
  setDefaultTemplate: (...a: unknown[]) => setDefaultTemplate(...a),
  setTemplateEnabled: (...a: unknown[]) => setTemplateEnabled(...a),
  rollBackTemplate: (...a: unknown[]) => rollBackTemplate(...a),
  deleteTemplate: (...a: unknown[]) => deleteTemplate(...a),
  publishTemplateVersion: (...a: unknown[]) => publishTemplateVersion(...a),
  discardTemplateVersion: (...a: unknown[]) => discardTemplateVersion(...a),
  startTemplateEdit: (...a: unknown[]) => startTemplateEdit(...a),
  startTemplateRefresh: (...a: unknown[]) => startTemplateRefresh(...a),
}))

import { ApiError } from '../api/client'
import { renderWithKit } from '../test/renderWithKit'
import TemplateDetailPage from './TemplateDetailPage'

const admin: MeDto = { id: 'u1', displayName: 'Dev', role: 'Admin', kind: 'Local', totpEnabled: false, isBreakGlass: false, roleManagedByIdp: false, maxUploadBytes: 268_435_456 }
const SYSMON = 'a'.repeat(64)
const MITM = 'b'.repeat(64)

const version = (over: Partial<TemplateVersionDto>): TemplateVersionDto => ({
  id: 'v-id', number: 1, origin: 'Imported', status: 'Ready', role: 'Pending', isCurrent: false, diskPresent: true,
  windowsEdition: null, windowsBuild: null, officePresent: null, agentVersion: null, sealedBy: null,
  sealedAt: '2026-10-24T10:00:00Z', note: null, sizeBytes: null, sourceMedia: [], taskCount: 0, ...over,
})

const v3 = version({
  id: 'g3', number: 3, origin: 'Edit', isCurrent: true, role: 'Current', windowsEdition: 'Windows 11 Pro', windowsBuild: '26300',
  officePresent: true, agentVersion: '1.0.0', sealedBy: 'basht0p', sizeBytes: 2 * 1024 ** 3, note: 'Added 7-Zip', parentVersionId: 'g2', parentNumber: 2,
  sysmonConfigSha256: SYSMON, mitmCaSha256: MITM, sourceMedia: [{ id: 'm1', name: 'Win11_26H2.iso' }, { id: 'm2', name: 'Office.iso' }], taskCount: 12,
})
const v2 = version({ id: 'g2', number: 2, origin: 'Refresh', role: 'RollbackTarget', taskCount: 3 })
const v1 = version({ id: 'g1', number: 1, origin: 'IsoBuild', status: 'Deleted', role: 'Deleted', diskPresent: false })

const golden = (over: Partial<AdminTemplateDto> = {}): AdminTemplateDto => ({
  id: 'win11-golden', displayName: 'Windows 11 Golden', description: null, isDefault: false, enabled: true,
  current: v3, versions: [v1, v3, v2], tasksLast30Days: 0, refreshDue: false, activeJobId: null, ...over,
})

const job = (over: Partial<TemplateJobDto> = {}): TemplateJobDto => ({
  id: 'job-1', kind: 'Edit', templateId: 'win11-golden', baseVersionNumber: 2, resultVersionNumber: 3, network: 'None', state: 'Published',
  message: null, failureReason: null, createdAt: '2026-10-24T10:00:00Z', endedAt: '2026-10-24T10:05:30Z', idleDeadline: null,
  requestedByName: 'Alice Admin', ...over,
})

const detail = (template: AdminTemplateDto = golden(), jobs: TemplateJobDto[] = []): TemplateDetailDto => ({ template, jobs })

const renderPage = (id = 'win11-golden') =>
  renderWithKit(
    <Routes>
      <Route path="/admin/templates/:id" element={<TemplateDetailPage me={admin} />} />
      <Route path="*" element={<p>elsewhere</p>} />
    </Routes>,
    { route: `/admin/templates/${id}` },
  )

const tick = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })
const answer = async (label: string) => userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: label }))
const versionItem = async (n: number) => screen.findByRole('listitem', { name: `v${n}` })

beforeEach(() => {
  vi.clearAllMocks()
  getAdminTemplate.mockResolvedValue(detail())
})
afterEach(() => vi.restoreAllMocks())

describe('TemplateDetailPage', () => {
  it('shows versions newest first with provenance, copyable hashes, media links and usage', async () => {
    renderPage()
    expect(await screen.findByRole('heading', { level: 1, name: 'Windows 11 Golden' })).toBeInTheDocument()
    expect(getAdminTemplate).toHaveBeenCalledWith('win11-golden')
    const list = screen.getByRole('list', { name: 'Versions' })
    const items = within(list).getAllByRole('listitem').filter((li) => li.parentElement === list)
    expect(items.map((li) => li.getAttribute('aria-label'))).toEqual(['v3', 'v2', 'v1'])

    const c = await versionItem(3)
    expect(within(c).getByText('Current')).toBeInTheDocument()
    expect(within(c).getByText('Edit')).toBeInTheDocument()
    expect(within(c).getByText('from v2')).toBeInTheDocument()
    expect(within(c).getByText(/sealed by basht0p/i)).toBeInTheDocument()
    expect(within(c).getByText('2.0 GiB')).toBeInTheDocument()
    expect(within(c).getByText('Added 7-Zip')).toBeInTheDocument()
    expect(within(c).getByText('Windows 11 Pro 26300')).toBeInTheDocument()
    expect(within(c).getByText('Office ✓')).toBeInTheDocument()
    expect(within(c).getByText('agent 1.0.0')).toBeInTheDocument()
    expect(within(c).getByText(SYSMON)).toBeInTheDocument()
    expect(within(c).getByText(MITM)).toBeInTheDocument()
    expect(within(c).getByRole('button', { name: 'Copy Sysmon config SHA-256' })).toBeInTheDocument()
    expect(within(c).getByRole('button', { name: 'Copy mitm CA SHA-256' })).toBeInTheDocument()
    expect(within(c).getByRole('link', { name: 'Win11_26H2.iso' })).toHaveAttribute('href', '/admin/media')
    expect(within(c).getByRole('link', { name: 'Office.iso' })).toHaveAttribute('href', '/admin/media')
    expect(within(c).getByText('12 tasks')).toBeInTheDocument()

    const r = await versionItem(2)
    expect(within(r).getByText('Rollback target')).toBeInTheDocument()
    expect(within(r).getByText('3 tasks')).toBeInTheDocument()
    // a Deleted version has no disk by design: no warning there
    expect(screen.queryByText('Disk missing')).toBeNull()
  })

  it('renders untrusted note and edition text literally', async () => {
    const odd = version({ ...v3, note: '<b>x</b>‮txt', windowsEdition: 'Pro​', windowsBuild: null })
    getAdminTemplate.mockResolvedValue(detail(golden({ current: odd, versions: [odd] })))
    renderPage()
    const c = await versionItem(3)
    expect(within(c).getByText('<b>x</b>\\u202Etxt')).toBeInTheDocument()
    expect(within(c).getByText('Pro\\u200B')).toBeInTheDocument()
  })

  it('shows a deleted source media name as plain text', async () => {
    const gone = version({ ...v3, sourceMedia: [{ id: 'm9', name: '(deleted)' }] })
    getAdminTemplate.mockResolvedValue(detail(golden({ current: gone, versions: [gone] })))
    renderPage()
    const c = await versionItem(3)
    expect(within(c).getByText('(deleted)')).toBeInTheDocument()
    expect(within(c).queryByRole('link', { name: '(deleted)' })).toBeNull()
  })

  it('publishes and discards a pending version through confirm dialogs', async () => {
    const p4 = version({ id: 'p4', number: 4, origin: 'Edit', role: 'Pending' })
    getAdminTemplate.mockResolvedValue(detail(golden({ versions: [p4, v3, v2] })))
    publishTemplateVersion.mockResolvedValue(undefined)
    discardTemplateVersion.mockResolvedValue(undefined)
    renderPage()
    const p = await versionItem(4)
    expect(within(p).getByText('Not published')).toBeInTheDocument()

    await userEvent.click(within(p).getByRole('button', { name: 'Publish v4' }))
    expect(await screen.findByRole('dialog', { name: 'Publish v4 of Windows 11 Golden?' })).toHaveTextContent('New tasks will use v4.')
    expect(publishTemplateVersion).not.toHaveBeenCalled()
    await answer('Publish')
    expect(publishTemplateVersion).toHaveBeenCalledWith('win11-golden', 'p4')
    expect(await screen.findByText('v4 published')).toBeInTheDocument()
    await waitFor(() => expect(getAdminTemplate).toHaveBeenCalledTimes(2))

    await userEvent.click(within(await versionItem(4)).getByRole('button', { name: 'Discard v4' }))
    expect(await screen.findByRole('dialog', { name: 'Discard v4 of Windows 11 Golden?' })).toHaveTextContent('It is never published and its disk is deleted.')
    await answer('Discard')
    expect(discardTemplateVersion).toHaveBeenCalledWith('win11-golden', 'p4')
    expect(await screen.findByText('v4 discarded')).toBeInTheDocument()
    await waitFor(() => expect(getAdminTemplate).toHaveBeenCalledTimes(3))
  })

  it('rolls back with the effect named in the dialog', async () => {
    rollBackTemplate.mockResolvedValue(undefined)
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Roll back' }))
    const dialog = await screen.findByRole('dialog', { name: 'Roll back Windows 11 Golden to v2?' })
    expect(dialog).toHaveTextContent('New tasks will use v2.')
    expect(dialog).toHaveTextContent('v3 will be retired')
    expect(rollBackTemplate).not.toHaveBeenCalled()
    await answer('Roll back')
    expect(rollBackTemplate).toHaveBeenCalledWith('win11-golden')
    expect(await screen.findByText('Rolled back to v2')).toBeInTheDocument()
    await waitFor(() => expect(getAdminTemplate).toHaveBeenCalledTimes(2))
  })

  it('disables Roll back with the existing reason when the target disk is missing', async () => {
    getAdminTemplate.mockResolvedValue(detail(golden({ versions: [v3, version({ ...v2, diskPresent: false })] })))
    renderPage()
    const r = await versionItem(2)
    expect(within(r).getByText('Disk missing')).toBeInTheDocument()
    const btn = screen.getByRole('button', { name: 'Roll back' })
    expect(btn).toBeDisabled()
    expect(btn).toHaveAttribute('title', 'Cannot roll back: the disk is missing for v2.')
  })

  it('confirms Set default and Disable, then toasts and refetches; a 409 is a toast', async () => {
    setDefaultTemplate.mockResolvedValue(undefined)
    setTemplateEnabled.mockRejectedValue(new ApiError(409, { title: 'The default template cannot be disabled.', status: 409 }))
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Set as default' }))
    expect(await screen.findByRole('dialog', { name: 'Make Windows 11 Golden the default?' })).toBeInTheDocument()
    await answer('Set as default')
    expect(setDefaultTemplate).toHaveBeenCalledWith('win11-golden')
    expect(await screen.findByText('Windows 11 Golden is the default')).toBeInTheDocument()
    await waitFor(() => expect(getAdminTemplate).toHaveBeenCalledTimes(2))

    await userEvent.click(screen.getByRole('button', { name: 'Disable' }))
    await answer('Disable')
    expect(setTemplateEnabled).toHaveBeenCalledWith('win11-golden', false)
    expect(await screen.findByText('The default template cannot be disabled.')).toBeInTheDocument()
  })

  it('lists jobs with requester and links to the job page', async () => {
    getAdminTemplate.mockResolvedValue(detail(golden(), [
      job({ id: 'j2', kind: 'Refresh', baseVersionNumber: 3, resultVersionNumber: null, state: 'Failed', failureReason: 'Verify failed.', endedAt: '2026-10-24T10:01:05Z' }),
      job({ id: 'j1' }),
      job({ id: 'j0', kind: 'Build', baseVersionNumber: null, resultVersionNumber: 1, requestedByName: null }),
    ]))
    renderPage()
    const table = await screen.findByRole('table', { name: 'Jobs' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(3)
    expect(rows[0]).toHaveTextContent('Refresh')
    expect(rows[0]).toHaveTextContent('v3 →')
    expect(within(rows[0]).getByText('Failed')).toBeInTheDocument()
    expect(rows[0]).toHaveTextContent('Verify failed.')
    expect(rows[0]).toHaveTextContent('1:05')
    expect(rows[1]).toHaveTextContent('v2 → v3')
    expect(rows[1]).toHaveTextContent('5:30')
    expect(rows[1]).toHaveTextContent('Alice Admin')
    expect(rows[2]).toHaveTextContent('new → v1')
    expect(within(rows[0]).getByRole('link', { name: 'Refresh' })).toHaveAttribute('href', '/admin/template-jobs/j2')
    expect(within(rows[1]).getByRole('link', { name: 'Edit' })).toHaveAttribute('href', '/admin/template-jobs/j1')
  })

  it('blocks Edit and Refresh while a job is active and links to it', async () => {
    getAdminTemplate.mockResolvedValue(detail(golden({ activeJobId: 'j5' }), [job({ id: 'j5', state: 'Configuring', endedAt: null })]))
    renderPage()
    const edit = await screen.findByRole('button', { name: 'Edit' })
    expect(edit).toBeDisabled()
    expect(edit).toHaveAttribute('title', 'An Edit job is already configuring for this template.')
    expect(screen.getByRole('link', { name: 'Open the job' })).toHaveAttribute('href', '/admin/template-jobs/j5')
  })

  it('opens the Edit dialog from the header', async () => {
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Edit' }))
    expect(screen.getByRole('dialog', { name: 'Edit Windows 11 Golden' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('shows Template not found for a 404', async () => {
    getAdminTemplate.mockRejectedValue(new ApiError(404, { title: 'Not found', status: 404 }))
    renderPage('nope')
    expect(await screen.findByText('Template not found')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Back to templates' })).toHaveAttribute('href', '/admin/templates')
  })

  it('never shows the previous template after the id changes', async () => {
    let resolveOther!: (d: TemplateDetailDto) => void
    getAdminTemplate.mockImplementation((id: string) => (id === 'other' ? new Promise((r) => { resolveOther = r }) : Promise.resolve(detail())))
    renderWithKit(
      <>
        <Link to="/admin/templates/other">go other</Link>
        <Routes>
          <Route path="/admin/templates/:id" element={<TemplateDetailPage me={admin} />} />
        </Routes>
      </>,
      { route: '/admin/templates/win11-golden' },
    )
    expect(await screen.findByRole('heading', { level: 1, name: 'Windows 11 Golden' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'go other' }))
    expect(screen.queryByRole('heading', { level: 1, name: 'Windows 11 Golden' })).toBeNull()
    await act(async () => { resolveOther(detail(golden({ id: 'other', displayName: 'Other' }))) })
    expect(await screen.findByRole('heading', { level: 1, name: 'Other' })).toBeInTheDocument()
  })

  it('shows a load error with Retry', async () => {
    getAdminTemplate.mockRejectedValueOnce(new Error('boom'))
    renderPage()
    expect(await screen.findByRole('alert')).toHaveTextContent('boom')
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Windows 11 Golden' })).toBeInTheDocument()
  })

  it('renders a template with no versions with actions disabled and reasons shown', async () => {
    getAdminTemplate.mockResolvedValue(detail(golden({ current: null, versions: [] })))
    deleteTemplate.mockResolvedValue(undefined)
    renderPage()
    const edit = await screen.findByRole('button', { name: 'Edit' })
    for (const name of ['Edit', 'Refresh components']) {
      const b = screen.getByRole('button', { name })
      expect(b).toBeDisabled()
      expect(b).toHaveAttribute('title', 'no version yet')
      expect(b).toHaveAccessibleDescription('no version yet')
    }
    expect(edit).toBeDisabled()
    const rb = screen.getByRole('button', { name: 'Roll back' })
    expect(rb).toBeDisabled()
    expect(rb).toHaveAccessibleDescription('No previous version to roll back to.')
    expect(screen.getByText('No versions yet')).toBeInTheDocument()
    expect(screen.getByText('No jobs yet')).toBeInTheDocument()

    const del = screen.getByRole('button', { name: 'Delete' })
    expect(del).toBeEnabled()
    await userEvent.click(del)
    expect(await screen.findByRole('dialog', { name: 'Delete Windows 11 Golden?' })).toBeInTheDocument()
    await answer('Delete')
    expect(deleteTemplate).toHaveBeenCalledWith('win11-golden')
    expect(await screen.findByText('elsewhere')).toBeInTheDocument() // back to the list after a delete
  })

  it('keeps Delete disabled with a reason while versions exist', async () => {
    renderPage()
    const del = await screen.findByRole('button', { name: 'Delete' })
    expect(del).toBeDisabled()
    expect(del).toHaveAccessibleDescription('Only a template with no versions can be deleted.')
  })

  it('shows no Publish or Discard on versions that are not Pending', async () => {
    renderPage()
    await versionItem(3)
    expect(screen.queryByRole('button', { name: /^Publish v/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /^Discard v/ })).toBeNull()
  })

  it('shows a 409 from Publish as a toast', async () => {
    const p4 = version({ id: 'p4', number: 4, role: 'Pending' })
    getAdminTemplate.mockResolvedValue(detail(golden({ versions: [p4, v3, v2] })))
    publishTemplateVersion.mockRejectedValue(new ApiError(409, { title: 'Only a held version can be published.', status: 409 }))
    renderPage()
    await userEvent.click(within(await versionItem(4)).getByRole('button', { name: 'Publish v4' }))
    await answer('Publish')
    expect(await screen.findByText('Only a held version can be published.')).toBeInTheDocument()
  })

  it('Retired-only and Pending-only histories leave Roll back disabled', async () => {
    const retired = version({ id: 'r2', number: 2, status: 'Retired', role: 'Retired' })
    getAdminTemplate.mockResolvedValue(detail(golden({ versions: [v3, retired] })))
    const { unmount } = renderPage()
    expect(await screen.findByRole('button', { name: 'Roll back' })).toBeDisabled()
    unmount()
    const pending = version({ id: 'p4', number: 4, origin: 'Edit', role: 'Pending' })
    getAdminTemplate.mockResolvedValue(detail(golden({ versions: [pending, v3] })))
    renderPage()
    await versionItem(4)
    expect(screen.getByRole('button', { name: 'Roll back' })).toBeDisabled()
  })

  it('takes the rollback target from the server role and names only it; the version rolled back from is retired', async () => {
    const v4 = version({ id: 'g4', number: 4, isCurrent: true, role: 'Current', origin: 'Edit' })
    const v3r = version({ id: 'r3', number: 3, status: 'Retired', role: 'Retired' })
    const v2t = version({ id: 'g2', number: 2, role: 'RollbackTarget' })
    getAdminTemplate.mockResolvedValue(detail(golden({ current: v4, versions: [v4, v3r, v2t] })))
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Roll back' }))
    const dialog = await screen.findByRole('dialog', { name: 'Roll back Windows 11 Golden to v2?' })
    expect(dialog).toHaveTextContent('New tasks will use v2.')
    expect(dialog).toHaveTextContent('v4 will be retired')
    expect(dialog.textContent).not.toContain('v3')
    expect(dialog.textContent).not.toContain('stays available')
    await answer('Cancel')
    expect(rollBackTemplate).not.toHaveBeenCalled()
  })

  it('keeps Delete disabled with the default-template reason, and with the active-job reason', async () => {
    getAdminTemplate.mockResolvedValue(detail(golden({ isDefault: true, current: null, versions: [] })))
    const { unmount } = renderPage()
    expect(await screen.findByRole('button', { name: 'Delete' })).toHaveAccessibleDescription('The default template cannot be deleted.')
    expect(screen.getByRole('button', { name: 'Delete' })).toBeDisabled()
    unmount()
    getAdminTemplate.mockResolvedValue(detail(golden({ current: null, versions: [], activeJobId: 'j5' }), [job({ id: 'j5', state: 'Configuring', endedAt: null })]))
    renderPage()
    const del = await screen.findByRole('button', { name: 'Delete' })
    expect(del).toBeDisabled()
    expect(del).toHaveAccessibleDescription('An Edit job is already configuring for this template.')
  })

  it('Delete Cancel makes no API call', async () => {
    getAdminTemplate.mockResolvedValue(detail(golden({ current: null, versions: [] })))
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Delete' }))
    await answer('Cancel')
    expect(deleteTemplate).not.toHaveBeenCalled()
  })

  it('a finished job does not block Edit and Refresh', async () => {
    getAdminTemplate.mockResolvedValue(detail(golden(), [job({ id: 'j2', state: 'Failed', failureReason: 'Verify failed.' })]))
    renderPage()
    expect(await screen.findByRole('button', { name: 'Edit' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Refresh components' })).toBeEnabled()
  })

  it('Edit defaults to network None and publishing', async () => {
    startTemplateEdit.mockResolvedValue(job({ id: 'job-e' }))
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Edit' }))
    await userEvent.click(screen.getByRole('button', { name: 'Start edit' }))
    expect(startTemplateEdit).toHaveBeenCalledWith('win11-golden', 'None', true)
  })

  it('shows an admin-entered name with control and bidi characters made visible', async () => {
    getAdminTemplate.mockResolvedValue(detail(golden({ displayName: 'Evil\u202E\u200Bx' })))
    renderPage()
    expect(await screen.findByRole('heading', { level: 1, name: 'Evil\\u202E\\u200Bx' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Set as default' }))
    expect(await screen.findByRole('dialog', { name: 'Make Evil\\u202E\\u200Bx the default?' })).toBeInTheDocument()
    await answer('Cancel')
    await userEvent.click(screen.getByRole('button', { name: 'Edit' }))
    expect(screen.getByRole('dialog', { name: 'Edit Evil\\u202E\\u200Bx' })).toBeInTheDocument()
  })

  describe('polling while a job runs', () => {
    afterEach(() => vi.useRealTimers())

    it('reloads when the job ends and re-enables Edit and Refresh', async () => {
      vi.useFakeTimers()
      getAdminTemplate.mockResolvedValueOnce(detail(golden({ activeJobId: 'j5' }), [job({ id: 'j5', state: 'Sealing', endedAt: null })]))
      getAdminTemplate.mockResolvedValue(detail(golden(), [job({ id: 'j5', state: 'Published' })]))
      renderPage()
      await tick(0)
      expect(screen.getByRole('button', { name: 'Edit' })).toBeDisabled()
      expect(getAdminTemplate).toHaveBeenCalledTimes(1)
      await tick(5000)
      expect(getAdminTemplate).toHaveBeenCalledTimes(2)
      expect(screen.getByRole('button', { name: 'Edit' })).toBeEnabled()
      expect(screen.getByRole('button', { name: 'Refresh components' })).toBeEnabled()
    })

    const running = () => detail(golden({ activeJobId: 'j5' }), [job({ id: 'j5', state: 'Sealing', endedAt: null })])
    const deferred = <T,>() => {
      let resolve!: (v: T) => void
      const promise = new Promise<T>((r) => { resolve = r })
      return { promise, resolve }
    }

    it('skips a tick while the tab is hidden', async () => {
      vi.useFakeTimers()
      getAdminTemplate.mockResolvedValue(running())
      renderPage()
      await tick(0)
      const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true)
      await tick(15000)
      expect(getAdminTemplate).toHaveBeenCalledTimes(1)
      hidden.mockReturnValue(false)
      await tick(5000)
      expect(getAdminTemplate).toHaveBeenCalledTimes(2)
    })

    it('skips a tick while a load is still in flight', async () => {
      vi.useFakeTimers()
      const slow = deferred<TemplateDetailDto>()
      getAdminTemplate.mockResolvedValueOnce(running())
      getAdminTemplate.mockReturnValueOnce(slow.promise)
      renderPage()
      await tick(0)
      await tick(5000)
      expect(getAdminTemplate).toHaveBeenCalledTimes(2)
      await tick(15000)
      expect(getAdminTemplate).toHaveBeenCalledTimes(2)
      slow.resolve(running())
      await tick(0)
      getAdminTemplate.mockResolvedValue(running())
      await tick(5000)
      expect(getAdminTemplate).toHaveBeenCalledTimes(3)
    })

    it('an older poll answer arriving after an action reload does not overwrite it', async () => {
      vi.useFakeTimers()
      const stale = deferred<TemplateDetailDto>()
      const p4 = version({ id: 'p4', number: 4, origin: 'Edit', role: 'Pending' })
      const before = detail(golden({ versions: [p4, v3, v2] }), [job({ id: 'j5', state: 'Sealing', endedAt: null })])
      getAdminTemplate.mockResolvedValueOnce(before) // initial
      getAdminTemplate.mockReturnValueOnce(stale.promise) // the poll, slow
      getAdminTemplate.mockResolvedValue(detail(golden(), [job({ id: 'j5', state: 'Published' })])) // the action's reload
      publishTemplateVersion.mockResolvedValue(undefined)
      renderPage()
      await tick(0)
      await tick(5000) // the poll starts and hangs
      fireEvent.click(within(screen.getByRole('listitem', { name: 'v4' })).getByRole('button', { name: 'Publish v4' }))
      await tick(0)
      fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Publish' }))
      await tick(0)
      await tick(0)
      expect(screen.queryByRole('listitem', { name: 'v4' })).toBeNull()
      stale.resolve(before) // the old answer lands last
      await tick(0)
      expect(screen.queryByRole('listitem', { name: 'v4' })).toBeNull()
    })

    it('keeps the last data and offers Retry when a reload fails', async () => {
      vi.useFakeTimers()
      getAdminTemplate.mockResolvedValueOnce(detail(golden({ activeJobId: 'j5' }), [job({ id: 'j5', state: 'Sealing', endedAt: null })]))
      getAdminTemplate.mockRejectedValue(new Error('boom'))
      renderPage()
      await tick(0)
      await tick(5000)
      expect(screen.getByRole('alert')).toHaveTextContent('Could not refresh the template (boom). Showing the last loaded versions.')
      expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
      expect(screen.getByRole('heading', { level: 1, name: 'Windows 11 Golden' })).toBeInTheDocument()
    })
  })
})
