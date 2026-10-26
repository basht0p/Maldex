import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { MeDto, UserDto } from '../api/types'

const api = {
  listUsers: vi.fn(), createUser: vi.fn(), updateUser: vi.fn(), resetUserPassword: vi.fn(),
  clearUserTotp: vi.fn(), getAuthSettings: vi.fn(), putAuthSettings: vi.fn(),
}
vi.mock('../api/client', async (orig) => ({
  ...(await orig<typeof import('../api/client')>()),
  listUsers: (...a: unknown[]) => api.listUsers(...a),
  createUser: (...a: unknown[]) => api.createUser(...a),
  updateUser: (...a: unknown[]) => api.updateUser(...a),
  resetUserPassword: (...a: unknown[]) => api.resetUserPassword(...a),
  clearUserTotp: (...a: unknown[]) => api.clearUserTotp(...a),
  getAuthSettings: (...a: unknown[]) => api.getAuthSettings(...a),
  putAuthSettings: (...a: unknown[]) => api.putAuthSettings(...a),
}))

import { ApiError } from '../api/client'
import { renderWithKit } from '../test/renderWithKit'
import UsersAdminPage from './UsersAdminPage'

const me: MeDto = { id: 'u0', displayName: 'Root', role: 'Admin', kind: 'Local', totpEnabled: false, isBreakGlass: true, roleManagedByIdp: false, maxUploadBytes: 1024 }
const user = (over: Partial<UserDto> = {}): UserDto => ({
  id: 'u1', userName: 'alice', displayName: 'Alice A', kind: 'Local', role: 'Analyst', isDisabled: false, totpEnabled: false,
  isBreakGlass: false, roleManagedByIdp: false, createdAt: '2026-10-01T00:00:00Z', lastSignInAt: null, ...over,
})
const rowOf = (name: string) => screen.getByRole('row', { name: new RegExp(name) })

beforeEach(() => {
  Object.values(api).forEach((f) => f.mockReset())
  api.listUsers.mockResolvedValue([
    user(),
    user({ id: 'u2', userName: 'root', displayName: 'Root', role: 'Admin', totpEnabled: true, isBreakGlass: true, lastSignInAt: '2026-10-25T10:00:00Z' }),
    user({ id: 'u3', userName: 'sso.bob', displayName: 'Bob', kind: 'Sso', role: 'Analyst', roleManagedByIdp: true, isDisabled: true }),
  ])
  api.getAuthSettings.mockResolvedValue({ requireTotpForLocal: false })
})

describe('UsersAdminPage', () => {
  it('lists users with kind, role, status, TOTP and break-glass badges and last sign-in', async () => {
    renderWithKit(<UsersAdminPage me={me} />)
    await screen.findByText('alice')
    const alice = within(rowOf('alice'))
    expect(alice.getByText('Local')).toBeInTheDocument()
    expect(alice.getByLabelText('Role of alice')).toHaveValue('Analyst')
    expect(alice.getByText('Active')).toBeInTheDocument()
    expect(alice.getByText('Never')).toBeInTheDocument()
    expect(alice.queryByText('Break-glass')).toBeNull()
    const root = within(rowOf('root'))
    expect(root.getByText('Break-glass')).toBeInTheDocument()
    expect(root.getByText('TOTP on')).toBeInTheDocument()
    expect(root.getByRole('time')).toHaveAttribute('datetime', '2026-10-25T10:00:00Z')
    const bob = within(rowOf('sso.bob'))
    expect(bob.getByText('SSO')).toBeInTheDocument()
    expect(bob.getByText('Disabled')).toBeInTheDocument()
  })

  it('new user dialog validates user name pattern and 12-character password, then creates', async () => {
    api.createUser.mockResolvedValue(user({ id: 'u9', userName: 'carol.d' }))
    const ui = userEvent.setup()
    renderWithKit(<UsersAdminPage me={me} />)
    await screen.findByText('alice')
    await ui.click(screen.getByRole('button', { name: 'New user' }))
    const dlg = screen.getByRole('dialog', { name: 'New user' })
    await ui.type(within(dlg).getByLabelText('User name'), 'bad name!')
    await ui.type(within(dlg).getByLabelText('Display name'), 'Carol D')
    await ui.type(within(dlg).getByLabelText('Password'), 'short')
    await ui.click(within(dlg).getByRole('button', { name: 'Create user' }))
    expect(within(dlg).getByText('User names may use letters, digits and . _ @ - (1 to 64 characters).')).toBeInTheDocument()
    expect(within(dlg).getByText('Use at least 12 characters.')).toBeInTheDocument()
    expect(api.createUser).not.toHaveBeenCalled()

    await ui.clear(within(dlg).getByLabelText('User name'))
    await ui.type(within(dlg).getByLabelText('User name'), 'carol.d')
    await ui.clear(within(dlg).getByLabelText('Password'))
    await ui.type(within(dlg).getByLabelText('Password'), 'twelve-chars-ok')
    await ui.click(within(dlg).getByRole('button', { name: 'Create user' }))
    await waitFor(() => expect(api.createUser).toHaveBeenCalledWith({
      userName: 'carol.d', displayName: 'Carol D', role: 'Analyst', password: 'twelve-chars-ok', mustChangePassword: true, isBreakGlass: false,
    }))
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'New user' })).toBeNull())
  })

  it('duplicate_user_name shows "That user name is taken."', async () => {
    api.createUser.mockRejectedValue(new ApiError(409, { error: 'duplicate_user_name' }))
    const ui = userEvent.setup()
    renderWithKit(<UsersAdminPage me={me} />)
    await screen.findByText('alice')
    await ui.click(screen.getByRole('button', { name: 'New user' }))
    const dlg = screen.getByRole('dialog', { name: 'New user' })
    await ui.type(within(dlg).getByLabelText('User name'), 'alice')
    await ui.type(within(dlg).getByLabelText('Display name'), 'Alice')
    await ui.type(within(dlg).getByLabelText('Password'), 'twelve-chars-ok')
    await ui.click(within(dlg).getByRole('button', { name: 'Create user' }))
    expect(await within(dlg).findByText('That user name is taken.')).toBeInTheDocument()
  })

  it('last_admin shows "Maldex needs at least one administrator who can sign in."', async () => {
    api.updateUser.mockRejectedValue(new ApiError(409, { error: 'last_admin' }))
    const ui = userEvent.setup()
    renderWithKit(<UsersAdminPage me={me} />)
    await screen.findByText('root')
    await ui.click(within(rowOf('root')).getByRole('button', { name: 'Disable root' }))
    expect(await screen.findByText('Maldex needs at least one administrator who can sign in.')).toBeInTheDocument()
  })

  it('role select is disabled with "Managed by your identity provider" when roleManagedByIdp', async () => {
    renderWithKit(<UsersAdminPage me={me} />)
    await screen.findByText('sso.bob')
    const bob = within(rowOf('sso.bob'))
    expect(bob.getByLabelText('Role of sso.bob')).toBeDisabled()
    expect(bob.getByText('Managed by your identity provider')).toBeInTheDocument()
    expect(within(rowOf('alice')).getByLabelText('Role of alice')).toBeEnabled()
  })

  it('changing the role sends it and refreshes the list', async () => {
    api.updateUser.mockResolvedValue(user({ role: 'Admin' }))
    renderWithKit(<UsersAdminPage me={me} />)
    await screen.findByText('alice')
    fireEvent.change(screen.getByLabelText('Role of alice'), { target: { value: 'Admin' } })
    await waitFor(() => expect(api.updateUser).toHaveBeenCalledWith('u1', { role: 'Admin' }))
  })

  it('reset password asks for a new password and confirms "They must change it at next sign-in."', async () => {
    api.resetUserPassword.mockResolvedValue(undefined)
    const ui = userEvent.setup()
    renderWithKit(<UsersAdminPage me={me} />)
    await screen.findByText('alice')
    await ui.click(within(rowOf('alice')).getByRole('button', { name: 'Reset password for alice' }))
    const dlg = screen.getByRole('dialog', { name: 'Reset password for alice' })
    await ui.type(within(dlg).getByLabelText('New password'), 'short')
    await ui.click(within(dlg).getByRole('button', { name: 'Reset password' }))
    expect(within(dlg).getByText('Use at least 12 characters.')).toBeInTheDocument()
    expect(api.resetUserPassword).not.toHaveBeenCalled()
    await ui.clear(within(dlg).getByLabelText('New password'))
    await ui.type(within(dlg).getByLabelText('New password'), 'a-new-long-password')
    await ui.click(within(dlg).getByRole('button', { name: 'Reset password' }))
    await waitFor(() => expect(api.resetUserPassword).toHaveBeenCalledWith('u1', 'a-new-long-password'))
    expect(await screen.findByText('They must change it at next sign-in.', { exact: false })).toBeInTheDocument()
  })

  it('disable / enable and clear TOTP call the API and refresh the row', async () => {
    api.updateUser.mockResolvedValue(user({ isDisabled: true }))
    api.clearUserTotp.mockResolvedValue(undefined)
    const ui = userEvent.setup()
    renderWithKit(<UsersAdminPage me={me} />)
    await screen.findByText('alice')
    api.listUsers.mockClear()
    await ui.click(within(rowOf('alice')).getByRole('button', { name: 'Disable alice' }))
    await waitFor(() => expect(api.updateUser).toHaveBeenCalledWith('u1', { isDisabled: true }))
    await waitFor(() => expect(api.listUsers).toHaveBeenCalledTimes(1))

    await ui.click(within(rowOf('sso.bob')).getByRole('button', { name: 'Enable sso.bob' }))
    await waitFor(() => expect(api.updateUser).toHaveBeenCalledWith('u3', { isDisabled: false }))

    await ui.click(within(rowOf('root')).getByRole('button', { name: 'Clear TOTP for root' }))
    // True whether or not TOTP is required for the user (only then are they asked to enrol again).
    expect(await screen.findByText(/Their authenticator and recovery codes stop working\./)).toBeInTheDocument()
    expect(screen.queryByText(/They will be asked to set up an authenticator again at next sign-in\.$/)).not.toBeInTheDocument()
    await ui.click(await screen.findByRole('button', { name: 'Clear TOTP' }))
    await waitFor(() => expect(api.clearUserTotp).toHaveBeenCalledWith('u2'))
    await waitFor(() => expect(api.listUsers.mock.calls.length).toBeGreaterThanOrEqual(3))
  })

  it('Require TOTP switch reads and writes settings', async () => {
    api.getAuthSettings.mockResolvedValue({ requireTotpForLocal: true })
    api.putAuthSettings.mockResolvedValue({ requireTotpForLocal: false })
    const ui = userEvent.setup()
    renderWithKit(<UsersAdminPage me={me} />)
    const sw = await screen.findByRole('switch', { name: 'Require TOTP for local accounts' })
    await waitFor(() => expect(sw).toBeChecked())
    await ui.click(sw)
    await waitFor(() => expect(api.putAuthSettings).toHaveBeenCalledWith({ requireTotpForLocal: false }))
    await waitFor(() => expect(sw).not.toBeChecked())
  })

  it('disables the Require TOTP switch while saving, so it cannot double-submit', async () => {
    let resolve!: (v: { requireTotpForLocal: boolean }) => void
    api.putAuthSettings.mockReturnValue(new Promise((r) => { resolve = r }))
    const ui = userEvent.setup()
    renderWithKit(<UsersAdminPage me={me} />)
    const sw = await screen.findByRole('switch', { name: 'Require TOTP for local accounts' })
    await waitFor(() => expect(sw).toBeEnabled())
    await ui.click(sw)
    await waitFor(() => expect(sw).toBeDisabled())
    await ui.click(sw)
    expect(api.putAuthSettings).toHaveBeenCalledTimes(1)
    resolve({ requireTotpForLocal: true })
    await waitFor(() => expect(sw).toBeEnabled())
  })

  it('trims the user name before validating and sending', async () => {
    api.createUser.mockResolvedValue(user({ id: 'u9', userName: 'dave' }))
    const ui = userEvent.setup()
    renderWithKit(<UsersAdminPage me={me} />)
    await screen.findByText('alice')
    await ui.click(screen.getByRole('button', { name: 'New user' }))
    const dlg = screen.getByRole('dialog', { name: 'New user' })
    await ui.type(within(dlg).getByLabelText('User name'), '  dave ')
    await ui.type(within(dlg).getByLabelText('Password'), 'twelve-chars-ok')
    await ui.click(within(dlg).getByRole('button', { name: 'Create user' }))
    await waitFor(() => expect(api.createUser).toHaveBeenCalledWith(expect.objectContaining({ userName: 'dave', displayName: 'dave' })))
  })

  it('shows an access notice to non-admins', () => {
    renderWithKit(<UsersAdminPage me={{ ...me, role: 'Analyst' }} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Administrator access is required')
    expect(api.listUsers).not.toHaveBeenCalled()
  })
})
