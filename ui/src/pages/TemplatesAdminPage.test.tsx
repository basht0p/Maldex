import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminTemplateDto, MeDto, TemplateVersionDto } from '../api/types'

const listAdminTemplates = vi.fn()
const setDefaultTemplate = vi.fn()
const setTemplateEnabled = vi.fn()
const rollBackTemplate = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  listAdminTemplates: (...a: unknown[]) => listAdminTemplates(...a),
  setDefaultTemplate: (...a: unknown[]) => setDefaultTemplate(...a),
  setTemplateEnabled: (...a: unknown[]) => setTemplateEnabled(...a),
  rollBackTemplate: (...a: unknown[]) => rollBackTemplate(...a),
}))

import { ApiError } from '../api/client'
import TemplatesAdminPage from './TemplatesAdminPage'

const admin: MeDto = { id: 'u1', displayName: 'Dev', role: 'Admin' }

const version = (over: Partial<TemplateVersionDto>): TemplateVersionDto => ({
  id: 'v-id', number: 1, origin: 'Imported', status: 'Ready', isCurrent: false, diskPresent: true,
  windowsEdition: null, windowsBuild: null, officePresent: null, agentVersion: null, sealedBy: null,
  sealedAt: '2026-10-24T10:00:00Z', note: null, sizeBytes: null, ...over,
})

const v3 = version({
  id: 'g3', number: 3, origin: 'Edit', isCurrent: true, windowsEdition: 'Windows 11 Pro', windowsBuild: '26300', agentVersion: '1.0.0', sealedBy: 'basht0p',
})
const v2 = version({ id: 'g2', number: 2, origin: 'Refresh', status: 'Retired' })
const v1 = version({ id: 'g1', number: 1, origin: 'IsoBuild', status: 'Deleted', diskPresent: false })

const golden = (over: Partial<AdminTemplateDto> = {}): AdminTemplateDto => ({
  id: 'win11-golden', displayName: 'Windows 11 Golden', description: null, isDefault: false, enabled: true,
  current: v3, versions: [v3, v2, v1], ...over,
})
const base = (over: Partial<AdminTemplateDto> = {}): AdminTemplateDto => {
  const cur = version({ id: 'b1', number: 1, isCurrent: true })
  return { id: 'win11-26h2', displayName: 'Windows 11 26H2', description: null, isDefault: true, enabled: true, current: cur, versions: [cur], ...over }
}

const renderPage = (me: MeDto | null = admin) => render(<TemplatesAdminPage me={me} />)
const region = (name: string) => screen.findByRole('region', { name })

beforeEach(() => {
  vi.clearAllMocks()
  listAdminTemplates.mockResolvedValue([base(), golden()])
})
afterEach(() => vi.restoreAllMocks())

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
    const rows = within(g).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(3)
    expect(rows[0]).toHaveTextContent(/v3.*Edit.*Ready/)
    expect(rows[1]).toHaveTextContent(/v2.*Refresh.*Retired.*Disk missing/)
    expect(rows[2]).toHaveTextContent(/v1.*IsoBuild.*Deleted/)
    // a Deleted version has no disk by design: no warning there
    expect(within(rows[2]).queryByText('Disk missing')).toBeNull()
    expect(within(g).getAllByText('Disk missing')).toHaveLength(1)
  })

  it('does not warn when every disk is present', async () => {
    renderPage()
    await region('Windows 11 Golden')
    expect(screen.queryByText('Disk missing')).toBeNull()
  })

  it('rolls back only after the admin confirms', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    rollBackTemplate.mockResolvedValue(golden())
    renderPage()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByRole('button', { name: 'Roll back' }))
    expect(confirm).toHaveBeenCalledTimes(1)
    expect(rollBackTemplate).toHaveBeenCalledWith('win11-golden')
    await waitFor(() => expect(listAdminTemplates).toHaveBeenCalledTimes(2))
  })

  it('does not roll back when the confirmation is declined', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderPage()
    const g = await region('Windows 11 Golden')
    await userEvent.click(within(g).getByRole('button', { name: 'Roll back' }))
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
    expect(await within(def).findByRole('alert')).toHaveTextContent('The default template cannot be disabled.')
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
  })
})
