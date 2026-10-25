import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MediaFileDto } from '../api/types'

const listMedia = vi.fn()
const deleteMedia = vi.fn()
const fetchMedia = vi.fn()
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  listMedia: (...a: unknown[]) => listMedia(...a),
  deleteMedia: (...a: unknown[]) => deleteMedia(...a),
  fetchMedia: (...a: unknown[]) => fetchMedia(...a),
}))
const uploadMedia = vi.fn()
vi.mock('../api/mediaUpload', () => ({ uploadMedia: (...a: unknown[]) => uploadMedia(...a) }))

import { ApiError } from '../api/client'
import { renderWithKit } from '../test/renderWithKit'
import MediaLibrary from './MediaLibrary'

const row = (over: Partial<MediaFileDto>): MediaFileDto => ({
  id: 'm1', kind: 'OsInstaller', fileName: 'win11.iso', size: 1000, receivedBytes: 1000, sha256: 'abcdef0123456789abcdef0123456789', source: 'Upload',
  status: 'Ready', images: [], addedBy: 'basht0p', addedAt: '2026-10-25T09:30:00Z', usedBy: [], ...over,
})
const win = row({
  id: 'win', images: [
    { index: 6, name: 'Windows 11 Pro', build: '26300.1', supported: true },
    { index: 1, name: 'Windows 11 Home', build: '26300.1', supported: false, unsupportedReason: 'Home cannot be used' },
  ], preferredImageIndex: 6, usedBy: ['Windows 11 Golden v1', 'job j1'],
  usedByRefs: [{ templateId: 'golden', templateName: 'Windows 11 Golden', versionNumber: 1, templateExists: true }],
})
const office = row({ id: 'off', kind: 'Application', fileName: 'office.img', application: 'Office', sha256: '1111222233334444' })
const partial = row({ id: 'part', fileName: 'big.iso', size: 1000, receivedBytes: 400, status: 'Receiving', sha256: null })
const checking = row({ id: 'chk', fileName: 'chk.iso', size: 1000, receivedBytes: 1000, status: 'Receiving', sha256: null })
const failed = row({ id: 'bad', fileName: 'bad.iso', status: 'Failed', failureReason: 'not a Windows installer', sha256: null })

const rowOf = (name: string) => screen.getByRole('row', { name: new RegExp(name.replace('.', '\\.')) })

beforeEach(() => {
  vi.clearAllMocks()
  listMedia.mockResolvedValue([win, office, partial, checking, failed])
})
let confirmSpy: ReturnType<typeof vi.spyOn>
beforeEach(() => { confirmSpy = vi.spyOn(window, 'confirm') })
afterEach(() => {
  expect(confirmSpy).not.toHaveBeenCalled() // confirmations are in-app dialogs, never window.confirm
  vi.restoreAllMocks()
})

describe('MediaLibrary', () => {
  it('shows kind, name, size, SHA-256 prefix, status, detected images, uploader, date and users', async () => {
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    expect(screen.getByRole('table', { name: 'Media' }).parentElement?.className).toMatch(/tableScroll/)
    const w = rowOf('win11.iso')
    expect(w).toHaveTextContent('OS installer')
    expect(w).toHaveTextContent('abcdef012345')
    expect(w).not.toHaveTextContent('abcdef0123456')
    expect(w).toHaveTextContent('Ready')
    expect(w).toHaveTextContent('Windows 11 Pro')
    expect(w).toHaveTextContent('Windows 11 Home')
    expect(w).toHaveTextContent('basht0p')
    expect(w.querySelector('time')).toHaveAttribute('datetime', '2026-10-25T09:30:00Z') // shown relative, exact time in the title
    expect(within(w).getByRole('button', { name: 'Copy SHA-256' })).toBeInTheDocument()
    expect(w).toHaveTextContent('Windows 11 Golden v1')
    expect(rowOf('office.img')).toHaveTextContent('Office')
  })

  it('shows Receiving with a percentage, checking when everything arrived, and a failure reason', async () => {
    renderWithKit(<MediaLibrary />)
    await screen.findByText('big.iso')
    expect(rowOf('big.iso')).toHaveTextContent('Receiving 40%')
    expect(rowOf('chk.iso')).toHaveTextContent('checking')
    expect(rowOf('chk.iso')).not.toHaveTextContent('Receiving')
    expect(rowOf('bad.iso')).toHaveTextContent('Failed: not a Windows installer')
  })

  it('Delete asks first, then deletes and reloads', async () => {
    deleteMedia.mockResolvedValue(undefined)
    renderWithKit(<MediaLibrary />)
    await screen.findByText('office.img')
    const del = within(rowOf('office.img')).getByRole('button', { name: 'Delete' })
    await userEvent.click(del)
    const dlg = await screen.findByRole('dialog', { name: 'Delete office.img?' })
    expect(dlg).toHaveTextContent('Its file is removed from the library.')
    await userEvent.click(within(dlg).getByRole('button', { name: 'Cancel' }))
    expect(deleteMedia).not.toHaveBeenCalled()
    await userEvent.click(del)
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }))
    expect(deleteMedia).toHaveBeenCalledWith('off')
    expect(await screen.findByText('office.img deleted')).toBeInTheDocument()
    await waitFor(() => expect(listMedia).toHaveBeenCalledTimes(2))
  })

  it('shows the 409 reason when the media is in use', async () => {
    deleteMedia.mockRejectedValue(new ApiError(409, { title: 'win11.iso is used by Windows 11 Golden v1.', status: 409 }))
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    await userEvent.click(within(rowOf('win11.iso')).getByRole('button', { name: 'Delete' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }))
    expect(await screen.findByText('win11.iso is used by Windows 11 Golden v1.')).toBeInTheDocument()
  })

  it('Upload sends the chosen file and kind, shows progress, and reloads when done', async () => {
    let report!: (r: number, s: number) => void
    let finish!: () => void
    uploadMedia.mockImplementation((_f: File, _k: string, onProgress: (r: number, s: number) => void) => {
      report = onProgress
      return new Promise<void>((resolve) => { finish = resolve })
    })
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    const group = screen.getByRole('group', { name: 'Upload' })
    const file = new File(['x'], 'new.iso')
    await userEvent.upload(within(group).getByLabelText('File'), file)
    await userEvent.selectOptions(within(group).getByLabelText('Kind'), 'Application')
    await userEvent.click(within(group).getByRole('button', { name: 'Upload' }))
    expect(uploadMedia).toHaveBeenCalledWith(file, 'Application', expect.any(Function), expect.any(AbortSignal), undefined)
    act(() => report(50, 100))
    expect(await screen.findByText('Uploading new.iso: 50%')).toBeInTheDocument()
    const loads = listMedia.mock.calls.length
    await act(async () => finish())
    await waitFor(() => expect(listMedia.mock.calls.length).toBeGreaterThan(loads))
    expect(screen.queryByText(/Uploading new.iso/)).toBeNull()
  })

  it('a failed upload shows why; a Receiving upload row offers Resume', async () => {
    uploadMedia.mockRejectedValue(new TypeError('Failed to fetch'))
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    const group = screen.getByRole('group', { name: 'Upload' })
    await userEvent.upload(within(group).getByLabelText('File'), new File(['x'], 'new.iso'))
    await userEvent.click(within(group).getByRole('button', { name: 'Upload' }))
    expect(await screen.findByText('Failed to fetch')).toBeInTheDocument()
    expect(within(rowOf('big.iso')).getByRole('button', { name: 'Resume' })).toBeEnabled()
    expect(within(rowOf('win11.iso')).queryByRole('button', { name: 'Resume' })).toBeNull()
    expect(within(rowOf('chk.iso')).queryByRole('button', { name: 'Resume' })).toBeNull()
  })

  it('Resume asks for the file again and requires the same name and size', async () => {
    uploadMedia.mockResolvedValue(undefined)
    renderWithKit(<MediaLibrary />)
    await screen.findByText('big.iso')
    const input = screen.getByLabelText('Resume file')
    await userEvent.click(within(rowOf('big.iso')).getByRole('button', { name: 'Resume' }))
    await userEvent.upload(input, new File([new Uint8Array(1000)], 'other.iso'))
    expect(await screen.findByText(/Choose the same file/)).toBeInTheDocument()
    expect(uploadMedia).not.toHaveBeenCalled()

    await userEvent.click(within(rowOf('big.iso')).getByRole('button', { name: 'Resume' }))
    await userEvent.upload(input, new File([new Uint8Array(999)], 'big.iso'))
    expect(uploadMedia).not.toHaveBeenCalled()

    const good = new File([new Uint8Array(1000)], 'big.iso')
    await userEvent.click(within(rowOf('big.iso')).getByRole('button', { name: 'Resume' }))
    await userEvent.upload(input, good)
    expect(uploadMedia).toHaveBeenCalledWith(good, 'OsInstaller', expect.any(Function), expect.any(AbortSignal), 'part')
  })

  it('Fetch from URL sends the url, kind and optional name', async () => {
    fetchMedia.mockResolvedValue(row({ id: 'f1' }))
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    const group = screen.getByRole('group', { name: 'Fetch from URL' })
    await userEvent.type(within(group).getByLabelText('URL'), 'https://example.test/win.iso')
    await userEvent.type(within(group).getByLabelText('Name (optional)'), 'win-eval.iso')
    await userEvent.click(within(group).getByRole('button', { name: 'Fetch' }))
    expect(fetchMedia).toHaveBeenCalledWith('https://example.test/win.iso', 'OsInstaller', 'win-eval.iso')
    await waitFor(() => expect(listMedia).toHaveBeenCalledTimes(2))
  })

  it('shows the server reason when a fetch is refused', async () => {
    fetchMedia.mockRejectedValue(new ApiError(400, { title: 'the address is private', status: 400 }))
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    const group = screen.getByRole('group', { name: 'Fetch from URL' })
    await userEvent.type(within(group).getByLabelText('URL'), 'http://10.0.0.1/x.iso')
    await userEvent.click(within(group).getByRole('button', { name: 'Fetch' }))
    expect(await screen.findByText('the address is private')).toBeInTheDocument()
  })

  describe('polling', () => {
    beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }))
    afterEach(() => vi.useRealTimers())

    it('polls every 2 s while a row is Receiving, one request at a time, and stops when none is', async () => {
      let release!: (v: MediaFileDto[]) => void
      renderWithKit(<MediaLibrary />)
      await screen.findByText('big.iso')
      expect(listMedia).toHaveBeenCalledTimes(1)
      listMedia.mockImplementationOnce(() => new Promise<MediaFileDto[]>((r) => { release = r }))
      await act(() => vi.advanceTimersByTimeAsync(2000))
      expect(listMedia).toHaveBeenCalledTimes(2)
      await act(() => vi.advanceTimersByTimeAsync(6000)) // still in flight: no further request
      expect(listMedia).toHaveBeenCalledTimes(2)
      await act(async () => release([win]))
      expect(await screen.findByText('win11.iso')).toBeInTheDocument()
      await act(() => vi.advanceTimersByTimeAsync(10000)) // nothing Receiving any more
      expect(listMedia).toHaveBeenCalledTimes(2)
    })
  })

  it('lists editions with the default marked', async () => {
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    const w = rowOf('win11.iso')
    expect(within(w).getByText(/^Editions/)).toBeInTheDocument()
    const t = within(w).getByRole('table', { name: 'Editions of win11.iso' })
    const pro = within(t).getByRole('row', { name: /Windows 11 Pro/ })
    expect(pro).toHaveTextContent('6')
    expect(pro).toHaveTextContent('26300.1')
    expect(pro).toHaveTextContent('Default')
    const home = within(t).getByRole('row', { name: /Windows 11 Home/ })
    expect(home).not.toHaveTextContent('Default')
    expect(home).toHaveTextContent('Home cannot be used')
    expect(within(rowOf('office.img')).queryByText(/^Editions/)).toBeNull()
  })

  it('links used-by entries and shows a deleted template as text', async () => {
    const gone = row({
      id: 'g', fileName: 'g.iso', usedBy: ['Old v2'],
      usedByRefs: [{ templateId: 'old', templateName: 'Old', versionNumber: 2, templateExists: false }],
    })
    listMedia.mockResolvedValue([win, gone])
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    const link = within(rowOf('win11.iso')).getByRole('link', { name: 'Windows 11 Golden v1' })
    expect(link).toHaveAttribute('href', '/admin/templates/golden')
    expect(rowOf('win11.iso')).toHaveTextContent('job j1')
    expect(within(rowOf('g.iso')).queryByRole('link')).toBeNull()
    expect(rowOf('g.iso')).toHaveTextContent('Old v2')
  })

  it('delete confirm lists the users', async () => {
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    await userEvent.click(within(rowOf('win11.iso')).getByRole('button', { name: 'Delete' }))
    const dlg = await screen.findByRole('dialog', { name: 'Delete win11.iso?' })
    expect(dlg).toHaveTextContent('Windows 11 Golden v1, job j1')
    expect(dlg).toHaveTextContent('refuse')
    await userEvent.click(within(dlg).getByRole('button', { name: 'Cancel' }))
  })

  it('shows upload speed and ETA when the upload reports them', async () => {
    uploadMedia.mockImplementation((_f: File, _k: string, onProgress: (r: number, s: number, rate: unknown) => void) => {
      onProgress(1024 * 1024, 4 * 1024 * 1024, { bytesPerSec: 1024 * 1024, etaSec: 3 })
      return new Promise<void>(() => undefined)
    })
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    const group = screen.getByRole('group', { name: 'Upload' })
    await userEvent.upload(within(group).getByLabelText('File'), new File(['x'], 'new.iso'))
    await userEvent.click(within(group).getByRole('button', { name: 'Upload' }))
    const status = await screen.findByText(/Uploading new\.iso: 25%/)
    expect(status).toHaveTextContent('1.0 MiB/s')
    expect(status).toHaveTextContent('ETA 0:03')
  })

  it('shows an unsupported edition once, with only the reason after the name', async () => {
    const home = row({ id: 'h', fileName: 'h.iso', images: [{ index: 1, name: 'Windows 11 Home', build: '26300.1', supported: false, unsupportedReason: 'Windows 11 Home is not supported: Home cannot be used' }] })
    listMedia.mockResolvedValue([home])
    renderWithKit(<MediaLibrary />)
    await screen.findByText('h.iso')
    const cell = rowOf('h.iso')
    expect(cell).toHaveTextContent('Home cannot be used')
    expect(cell.textContent?.match(/Windows 11 Home/g)).toHaveLength(1)
  })

  it('a refresh requested while another is in flight runs once more when it ends', async () => {
    let release!: (v: MediaFileDto[]) => void
    deleteMedia.mockResolvedValue(undefined)
    listMedia.mockResolvedValue([win, office])
    renderWithKit(<MediaLibrary />)
    await screen.findByText('win11.iso')
    listMedia.mockImplementationOnce(() => new Promise<MediaFileDto[]>((r) => { release = r })) // the refresh after the first delete
    listMedia.mockResolvedValue([office])
    await userEvent.click(within(rowOf('office.img')).getByRole('button', { name: 'Delete' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(listMedia).toHaveBeenCalledTimes(2))
    await userEvent.click(within(rowOf('win11.iso')).getByRole('button', { name: 'Delete' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }))
    expect(listMedia).toHaveBeenCalledTimes(2) // dropped for now
    await act(async () => release([win, office])) // the stale answer still lists the deleted row
    await waitFor(() => expect(listMedia).toHaveBeenCalledTimes(3)) // the queued trailing refresh
    await waitFor(() => expect(screen.queryByText('win11.iso')).toBeNull())
  })

  it('shows a load error', async () => {
    listMedia.mockRejectedValue(new ApiError(500, { title: 'Boom' }))
    renderWithKit(<MediaLibrary />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Boom')
    listMedia.mockResolvedValue([win])
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByText('win11.iso')).toBeInTheDocument()
  })
})
