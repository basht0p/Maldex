import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { renderWithKit } from '../test/renderWithKit'
import AccountPage from './AccountPage'

vi.mock('qrcode', () => {
  const toDataURL = vi.fn(async () => 'data:image/png;base64,QR')
  return { default: { toDataURL }, toDataURL }
})

const server = setupServer()
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

const me = (over: Record<string, unknown> = {}) =>
  http.get('/api/me', () => HttpResponse.json({
    id: 'u1', displayName: 'Pat', role: 'Analyst', maxUploadBytes: 1, kind: 'Local', totpEnabled: false, isBreakGlass: false, roleManagedByIdp: false, ...over,
  }))

describe('AccountPage', () => {
  it('local user can change password and set up the authenticator', async () => {
    const pw: unknown[] = []
    server.use(
      me(),
      http.post('/api/auth/password', async ({ request }) => { pw.push(await request.json()); return HttpResponse.json({ next: 'done' }) }),
      http.post('/api/auth/totp/setup', () => HttpResponse.json({ sharedKey: 'KEY123', otpauthUri: 'otpauth://totp/x?secret=KEY123' })),
      http.post('/api/auth/totp/confirm', () => HttpResponse.json({ recoveryCodes: ['R1-AAAA'], next: 'done' })),
    )
    const user = userEvent.setup()
    renderWithKit(<AccountPage />)
    const pwSection = await screen.findByRole('region', { name: 'Password' })
    await user.type(within(pwSection).getByLabelText('Current password'), 'old-password-1')
    await user.type(within(pwSection).getByLabelText('New password'), 'new-password-123')
    await user.type(within(pwSection).getByLabelText('Repeat new password'), 'new-password-123')
    await user.click(within(pwSection).getByRole('button', { name: 'Change password' }))
    expect(await screen.findByText('Password changed.')).toBeInTheDocument()
    expect(pw).toEqual([{ currentPassword: 'old-password-1', newPassword: 'new-password-123' }])

    const totp = screen.getByRole('region', { name: 'Authenticator' })
    await user.click(within(totp).getByRole('button', { name: 'Set up authenticator' }))
    expect(await screen.findByAltText('Authenticator QR code')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Authentication code'), '123456')
    await user.click(screen.getByRole('button', { name: 'Confirm' }))
    expect(await screen.findByText('R1-AAAA')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'I have saved these codes' }))
    expect(await screen.findByText('Authenticator is on.')).toBeInTheDocument()
  })

  it('remove authenticator asks for the password and shows totp_required as "Your administrator requires an authenticator for local accounts."', async () => {
    const bodies: unknown[] = []
    server.use(
      me({ totpEnabled: true }),
      http.delete('/api/auth/totp', async ({ request }) => {
        bodies.push(await request.json())
        return HttpResponse.json({ error: 'totp_required' }, { status: 409 })
      }),
    )
    const user = userEvent.setup()
    renderWithKit(<AccountPage />)
    const totp = await screen.findByRole('region', { name: 'Authenticator' })
    await user.click(within(totp).getByRole('button', { name: 'Remove authenticator' }))
    await user.type(within(totp).getByLabelText('Password'), 'pw-123456789012')
    await user.click(within(totp).getByRole('button', { name: 'Remove' }))
    expect(await within(totp).findByRole('alert')).toHaveTextContent('Your administrator requires an authenticator for local accounts.')
    expect(bodies).toEqual([{ password: 'pw-123456789012' }])
  })

  it('regenerates recovery codes after asking for the password', async () => {
    server.use(me({ totpEnabled: true }), http.post('/api/auth/totp/recovery-codes', () => HttpResponse.json({ recoveryCodes: ['N1-AAAA', 'N2-BBBB'] })))
    const user = userEvent.setup()
    renderWithKit(<AccountPage />)
    const totp = await screen.findByRole('region', { name: 'Authenticator' })
    await user.click(within(totp).getByRole('button', { name: 'Regenerate recovery codes' }))
    await user.type(within(totp).getByLabelText('Password'), 'pw-123456789012')
    await user.click(within(totp).getByRole('button', { name: 'Generate' }))
    expect(await screen.findByText('N2-BBBB')).toBeInTheDocument()
  })

  it('SSO user sees provider, role and "managed by your identity provider"', async () => {
    server.use(me({ kind: 'Sso', role: 'Admin', roleManagedByIdp: true, displayName: 'Sam' }))
    renderWithKit(<AccountPage />)
    expect(await screen.findByText('Sam')).toBeInTheDocument()
    expect(screen.getByText('Single sign-on')).toBeInTheDocument()
    expect(screen.getByText('Admin')).toBeInTheDocument()
    expect(screen.getByText(/managed by your identity provider/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Change password' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Authenticator' })).not.toBeInTheDocument()
  })
})
