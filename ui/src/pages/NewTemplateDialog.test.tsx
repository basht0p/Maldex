import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AdminTemplateDto, MediaFileDto } from '../api/types'

const listMedia = vi.fn()
const startTemplateBuild = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  listMedia: (...a: unknown[]) => listMedia(...a),
  startTemplateBuild: (...a: unknown[]) => startTemplateBuild(...a),
}))

import { ApiError } from '../api/client'
import NewTemplateDialog from './NewTemplateDialog'

const media = (over: Partial<MediaFileDto>): MediaFileDto => ({
  id: 'm', kind: 'OsInstaller', fileName: 'win.iso', size: 10, receivedBytes: 10, sha256: 'ab', source: 'Upload', status: 'Ready',
  images: [], addedBy: 'dev', addedAt: '2026-10-25T10:00:00Z', usedBy: [], ...over,
})
const winIso = media({
  id: 'os1', fileName: 'win11.iso', preferredImageIndex: 6, images: [
    { index: 1, name: 'Windows 11 Home', build: '26300', supported: false, unsupportedReason: 'Home cannot be used' },
    { index: 6, name: 'Windows 11 Pro', build: '26300', supported: true },
    { index: 4, name: 'Windows 11 Enterprise', build: '26300', supported: true },
  ],
})
const receivingIso = media({ id: 'os2', fileName: 'half.iso', status: 'Receiving', receivedBytes: 5 })
const failedIso = media({ id: 'os3', fileName: 'bad.iso', status: 'Failed' })
const office = media({ id: 'off1', kind: 'Application', fileName: 'office.img', application: 'Office' })
const otherApp = media({ id: 'app2', kind: 'Application', fileName: 'tools.iso', application: null })
const template = (id: string, displayName: string): AdminTemplateDto => ({ id, displayName, isDefault: false, enabled: true, current: null, versions: [] })

const onClose = vi.fn()
const onStarted = vi.fn()
const renderDialog = () => render(<NewTemplateDialog templates={[template('win11-golden', 'Windows 11 Golden')]} onClose={onClose} onStarted={onStarted} />)

beforeEach(() => {
  vi.clearAllMocks()
  listMedia.mockResolvedValue([winIso, receivingIso, failedIso, office, otherApp])
  startTemplateBuild.mockResolvedValue({ id: 'job-b' })
})

describe('NewTemplateDialog', () => {
  it('offers only Ready installers and Ready Office media', async () => {
    renderDialog()
    const os = await screen.findByLabelText('OS media')
    expect(Array.from((os as HTMLSelectElement).options).map((o) => o.textContent)).toEqual(['win11.iso'])
    const off = screen.getByLabelText('Office media (optional)') as HTMLSelectElement
    expect(Array.from(off.options).map((o) => o.textContent)).toEqual(['None', 'office.img'])
  })

  it('defaults the edition to the preferred image and disables unsupported ones with their reason', async () => {
    renderDialog()
    const edition = (await screen.findByLabelText('Edition')) as HTMLSelectElement
    expect(edition.value).toBe('6')
    const home = Array.from(edition.options).find((o) => o.value === '1')!
    expect(home).toBeDisabled()
    expect(home.textContent).toContain('Home cannot be used')
    expect(Array.from(edition.options).find((o) => o.value === '4')).toBeEnabled()
  })

  it('starts a build of a new template with the right body', async () => {
    renderDialog()
    await screen.findByLabelText('OS media')
    await userEvent.type(screen.getByLabelText('Template id'), 'win11-eval')
    await userEvent.type(screen.getByLabelText('Display name'), 'Windows 11 Eval')
    await userEvent.selectOptions(screen.getByLabelText('Edition'), '4')
    await userEvent.selectOptions(screen.getByLabelText('Office media (optional)'), 'off1')
    expect(screen.getByLabelText('Publish when done')).toBeChecked()
    await userEvent.click(screen.getByLabelText('Publish when done'))
    await userEvent.click(screen.getByRole('button', { name: 'Start build' }))
    expect(startTemplateBuild).toHaveBeenCalledWith({
      templateId: 'win11-eval', displayName: 'Windows 11 Eval', osMediaId: 'os1', editionIndex: 4, applicationMediaIds: ['off1'], publishOnSuccess: false,
    })
    expect(onStarted).toHaveBeenCalledWith({ id: 'job-b' })
  })

  it('building into an existing template sends its id and no display name, and no Office by default', async () => {
    renderDialog()
    await screen.findByLabelText('OS media')
    await userEvent.selectOptions(screen.getByLabelText('Template'), 'win11-golden')
    expect(screen.queryByLabelText('Template id')).toBeNull()
    expect(screen.queryByLabelText('Display name')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Start build' }))
    expect(startTemplateBuild).toHaveBeenCalledWith({
      templateId: 'win11-golden', osMediaId: 'os1', editionIndex: 6, applicationMediaIds: [], publishOnSuccess: true,
    })
  })

  it('cannot start without an id and a name for a new template', async () => {
    renderDialog()
    await screen.findByLabelText('OS media')
    expect(screen.getByRole('button', { name: 'Start build' })).toBeDisabled()
  })

  it('shows the server reason inside the dialog', async () => {
    startTemplateBuild.mockRejectedValue(new ApiError(409, { title: 'Not enough free disk space.', status: 409 }))
    renderDialog()
    await screen.findByLabelText('OS media')
    await userEvent.type(screen.getByLabelText('Template id'), 'x')
    await userEvent.type(screen.getByLabelText('Display name'), 'X')
    await userEvent.click(screen.getByRole('button', { name: 'Start build' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Not enough free disk space.')
    expect(onStarted).not.toHaveBeenCalled()
  })

  it('says so when there is no installer media yet', async () => {
    listMedia.mockResolvedValue([])
    renderDialog()
    expect(await screen.findByText(/no ready installer media/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Start build' })).toBeDisabled()
  })
})
