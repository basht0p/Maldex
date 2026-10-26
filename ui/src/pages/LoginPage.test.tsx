import { StrictMode } from 'react'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { Route, Routes, useLocation } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { nav } from '../auth/session'
import { renderWithKit } from '../test/renderWithKit'
import LoginPage from './LoginPage'

vi.mock('qrcode', () => {
  const toDataURL = vi.fn(async () => 'data:image/png;base64,QR')
  return { default: { toDataURL }, toDataURL }
})

const server = setupServer()
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => { server.resetHandlers(); vi.restoreAllMocks() })
afterAll(() => server.close())

function Where() {
  const l = useLocation()
  return <p data-testid="where">{l.pathname}{l.search}</p>
}
function renderLogin(route = '/login') {
  return renderWithKit(
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="*" element={<Where />} />
    </Routes>,
    { route },
  )
}
const config = (c: { passwordLogin: boolean; sso: { displayName: string } | null }) => http.get('/api/auth/config', () => HttpResponse.json(c))
const loginReply = (body: object, status = 200) => http.post('/api/auth/login', () => HttpResponse.json(body, { status }))
async function signIn(user: ReturnType<typeof userEvent.setup>) {
  await user.type(await screen.findByLabelText('User name'), 'pat')
  await user.type(screen.getByLabelText('Password'), 'hunter2hunter2')
  await user.click(screen.getByRole('button', { name: 'Sign in' }))
}

describe('LoginPage', () => {
  it('shows the password form and SSO button when both are enabled', async () => {
    server.use(config({ passwordLogin: true, sso: { displayName: 'Contoso' } }))
    renderLogin()
    expect(await screen.findByLabelText('User name')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sign in with Contoso' })).toBeInTheDocument()
    expect(screen.getByText('MAL', { exact: false })).toBeInTheDocument()
  })

  it('shows only the SSO button when password login is off, and the form with ?local=1', async () => {
    server.use(config({ passwordLogin: false, sso: { displayName: 'Contoso' } }))
    const first = renderLogin()
    expect(await screen.findByRole('button', { name: 'Sign in with Contoso' })).toBeInTheDocument()
    expect(screen.queryByLabelText('User name')).not.toBeInTheDocument()
    first.unmount()
    renderLogin('/login?local=1')
    expect(await screen.findByLabelText('User name')).toBeInTheDocument()
  })

  it('says so when no sign-in method is configured', async () => {
    server.use(config({ passwordLogin: false, sso: null }))
    renderLogin()
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign-in is not configured. Ask an administrator.')
  })

  it.each([
    [401, { error: 'invalid' }, 'Wrong user name or password.'],
    [401, { error: 'locked' }, 'Too many failed attempts. Try again in 15 minutes.'],
    [401, { error: 'disabled' }, 'This account is disabled.'],
    [401, { error: 'password_login_disabled' }, 'Password sign-in is turned off. Use single sign-on.'],
    [429, { error: 'rate_limited' }, 'Too many sign-in attempts. Wait a minute and try again.'],
  ])('login %i %j shows %s', async (status, body, text) => {
    server.use(config({ passwordLogin: true, sso: null }), loginReply(body, status))
    renderLogin()
    await signIn(userEvent.setup())
    expect(await screen.findByRole('alert')).toHaveTextContent(text)
  })

  it.each([
    ['no_role', 'Your account has no Maldex role. Ask an administrator.'],
    ['sso_failed', 'Single sign-on failed. Try again or contact an administrator.'],
    ['disabled', 'This account is disabled.'],
  ])('?error=%s shows the message', async (error, text) => {
    server.use(config({ passwordLogin: true, sso: { displayName: 'Contoso' } }))
    renderLogin(`/login?error=${error}`)
    expect(await screen.findByRole('alert')).toHaveTextContent(text)
  })

  it('totp step accepts a code and a recovery code', async () => {
    const codes: string[] = []
    server.use(
      config({ passwordLogin: true, sso: null }),
      loginReply({ next: 'totp' }),
      http.post('/api/auth/login/totp', async ({ request }) => {
        const { code } = (await request.json()) as { code: string }
        codes.push(code)
        return code === '123456' ? HttpResponse.json({ error: 'invalid' }, { status: 401 }) : HttpResponse.json({ next: 'done' })
      }),
    )
    const user = userEvent.setup()
    renderLogin()
    await signIn(user)
    const input = await screen.findByLabelText('Authentication code or recovery code')
    await user.type(input, '123456')
    await user.click(screen.getByRole('button', { name: 'Verify' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('That code is not valid.')
    await user.clear(input)
    await user.type(input, 'ABCDE-FGHIJ')
    await user.click(screen.getByRole('button', { name: 'Verify' }))
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/'))
    expect(codes).toEqual(['123456', 'ABCDE-FGHIJ'])
  })

  it('change-password step enforces 12 characters and matching repeat before calling the API', async () => {
    const bodies: unknown[] = []
    server.use(
      config({ passwordLogin: true, sso: null }),
      loginReply({ next: 'change-password' }),
      http.post('/api/auth/password', async ({ request }) => { bodies.push(await request.json()); return HttpResponse.json({ next: 'done' }) }),
    )
    const user = userEvent.setup()
    renderLogin()
    await signIn(user)
    await user.type(await screen.findByLabelText('New password'), 'short')
    await user.type(screen.getByLabelText('Repeat new password'), 'short')
    await user.click(screen.getByRole('button', { name: 'Change password' }))
    expect(screen.getByRole('alert')).toHaveTextContent('The new password must be at least 12 characters.')
    expect(bodies).toEqual([])
    await user.clear(screen.getByLabelText('New password'))
    await user.clear(screen.getByLabelText('Repeat new password'))
    await user.type(screen.getByLabelText('New password'), 'a-long-enough-pw')
    await user.type(screen.getByLabelText('Repeat new password'), 'a-long-enough-px')
    await user.click(screen.getByRole('button', { name: 'Change password' }))
    expect(screen.getByRole('alert')).toHaveTextContent('The passwords do not match.')
    expect(bodies).toEqual([])
    await user.clear(screen.getByLabelText('Repeat new password'))
    await user.type(screen.getByLabelText('Repeat new password'), 'a-long-enough-pw')
    await user.click(screen.getByRole('button', { name: 'Change password' }))
    await waitFor(() => expect(bodies).toEqual([{ currentPassword: 'hunter2hunter2', newPassword: 'a-long-enough-pw' }]))
    await waitFor(() => expect(screen.getByTestId('where')).toBeInTheDocument())
  })

  it('enrol-totp step shows the QR code and key, confirms, then shows recovery codes once with copy and download', async () => {
    server.use(
      config({ passwordLogin: true, sso: null }),
      loginReply({ next: 'enrol-totp' }),
      http.post('/api/auth/totp/setup', () => HttpResponse.json({ sharedKey: 'JBSWY3DPEHPK3PXP', otpauthUri: 'otpauth://totp/Maldex:pat?secret=JBSWY3DPEHPK3PXP' })),
      http.post('/api/auth/totp/confirm', () => HttpResponse.json({ recoveryCodes: ['AAAAA-11111', 'BBBBB-22222'], next: 'done' })),
    )
    const user = userEvent.setup()
    renderLogin()
    await signIn(user)
    expect(await screen.findByAltText('Authenticator QR code')).toHaveAttribute('src', 'data:image/png;base64,QR')
    expect(screen.getByText('JBSWY3DPEHPK3PXP')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Authentication code'), '654321')
    await user.click(screen.getByRole('button', { name: 'Confirm' }))
    expect(await screen.findByText('AAAAA-11111')).toBeInTheDocument()
    expect(screen.getByText('BBBBB-22222')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy recovery codes' })).toBeInTheDocument()
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:codes')
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    await user.click(screen.getByRole('button', { name: 'Download' }))
    expect((click.mock.contexts[0] as HTMLAnchorElement).download).toBe('maldex-recovery-codes.txt')
    expect(create).toHaveBeenCalledOnce()
    // Not signed in to the app until the codes are acknowledged.
    expect(screen.queryByTestId('where')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'I have saved these codes' }))
    await waitFor(() => expect(screen.getByTestId('where')).toBeInTheDocument())
  })

  const expired = (path: string) => http.post(path, () => HttpResponse.json({ error: 'unauthenticated' }, { status: 401 }))
  const timedOut = 'Your sign-in timed out; sign in again.'

  it.each([
    ['totp', '/api/auth/login/totp'],
    ['change-password', '/api/auth/password'],
    ['enrol-totp', '/api/auth/totp/setup'],
  ])('an expired partial session at the %s step goes back to the credentials', async (next, path) => {
    server.use(config({ passwordLogin: true, sso: null }), loginReply({ next }), expired(path))
    const user = userEvent.setup()
    renderLogin()
    await signIn(user)
    if (next === 'totp') {
      await user.type(await screen.findByLabelText('Authentication code or recovery code'), '123456')
      await user.click(screen.getByRole('button', { name: 'Verify' }))
    } else if (next === 'change-password') {
      await user.type(await screen.findByLabelText('New password'), 'a-long-enough-pw')
      await user.type(screen.getByLabelText('Repeat new password'), 'a-long-enough-pw')
      await user.click(screen.getByRole('button', { name: 'Change password' }))
    }
    expect(await screen.findByRole('alert')).toHaveTextContent(timedOut)
    expect(screen.getByLabelText('User name')).toBeInTheDocument()
    expect(screen.getByLabelText('Password')).toHaveValue('')
  })

  it('an expired partial session at the enrolment confirm goes back to the credentials', async () => {
    server.use(
      config({ passwordLogin: true, sso: null }),
      loginReply({ next: 'enrol-totp' }),
      http.post('/api/auth/totp/setup', () => HttpResponse.json({ sharedKey: 'JBSWY3DPEHPK3PXP', otpauthUri: 'otpauth://totp/Maldex:pat?secret=JBSWY3DPEHPK3PXP' })),
      expired('/api/auth/totp/confirm'),
    )
    const user = userEvent.setup()
    renderLogin()
    await signIn(user)
    await user.type(await screen.findByLabelText('Authentication code'), '654321')
    await user.click(screen.getByRole('button', { name: 'Confirm' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(timedOut)
    expect(screen.getByLabelText('User name')).toBeInTheDocument()
  })

  it('a too-long new password is explained', async () => {
    server.use(
      config({ passwordLogin: true, sso: null }),
      loginReply({ next: 'change-password' }),
      http.post('/api/auth/password', () => HttpResponse.json({ error: 'password_too_long' }, { status: 400 })),
    )
    const user = userEvent.setup()
    renderLogin()
    await signIn(user)
    await user.type(await screen.findByLabelText('New password'), 'a-long-enough-pw')
    await user.type(screen.getByLabelText('Repeat new password'), 'a-long-enough-pw')
    await user.click(screen.getByRole('button', { name: 'Change password' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The new password must be at most 256 characters.')
  })

  it('enrolment starts the authenticator setup once, also under StrictMode', async () => {
    let setups = 0
    server.use(
      config({ passwordLogin: true, sso: null }),
      loginReply({ next: 'enrol-totp' }),
      http.post('/api/auth/totp/setup', () => {
        setups++
        return HttpResponse.json({ sharedKey: `KEY${setups}`, otpauthUri: `otpauth://totp/Maldex:pat?secret=KEY${setups}` })
      }),
    )
    const user = userEvent.setup()
    renderWithKit(<StrictMode><Routes><Route path="/login" element={<LoginPage />} /></Routes></StrictMode>, { route: '/login' })
    await signIn(user)
    expect(await screen.findByText('KEY1')).toBeInTheDocument()
    expect(setups).toBe(1)
  })

  it('done navigates to returnUrl', async () => {
    server.use(config({ passwordLogin: true, sso: null }), loginReply({ next: 'done' }))
    renderLogin('/login?returnUrl=%2Ftasks%2Ft1%3Ftab%3Dnet')
    await signIn(userEvent.setup())
    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent('/tasks/t1?tab=net'))
  })

  it('login ignores a non-local returnUrl', async () => {
    server.use(config({ passwordLogin: true, sso: null }), loginReply({ next: 'done' }))
    renderLogin('/login?returnUrl=%2F%2Fevil.example')
    await signIn(userEvent.setup())
    await waitFor(() => expect(screen.getByTestId('where').textContent).toBe('/'))
  })

  it('SSO button navigates to ssoStartUrl(returnUrl)', async () => {
    server.use(config({ passwordLogin: true, sso: { displayName: 'Contoso' } }))
    const assign = vi.spyOn(nav, 'assign').mockImplementation(() => undefined)
    renderLogin('/login?returnUrl=%2Ftasks%2Ft1')
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Sign in with Contoso' }))
    expect(assign).toHaveBeenCalledWith('/api/auth/sso/start?returnUrl=%2Ftasks%2Ft1')
  })
})
