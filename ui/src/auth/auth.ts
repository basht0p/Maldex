import {
  type AccountInfo,
  InteractionRequiredAuthError,
  PublicClientApplication,
} from '@azure/msal-browser'

export type AuthMode = 'dev' | 'entra'

/** `dev` (default): the server authenticates without a token. `entra`: sign in with Microsoft Entra ID and send a bearer token. */
export function getAuthMode(): AuthMode {
  return import.meta.env.VITE_AUTH_MODE === 'entra' ? 'entra' : 'dev'
}

let msal: PublicClientApplication | undefined

function scopes(): string[] {
  const scope = import.meta.env.VITE_API_SCOPE
  return scope ? [scope] : []
}

/** The shared MSAL instance (entra mode only). Created lazily; call {@link initAuth} before first use. */
export function getMsal(): PublicClientApplication {
  if (!msal) {
    const clientId = import.meta.env.VITE_ENTRA_CLIENT_ID
    const tenantId = import.meta.env.VITE_ENTRA_TENANT_ID
    if (!clientId || !tenantId) throw new Error('VITE_ENTRA_CLIENT_ID and VITE_ENTRA_TENANT_ID are required when VITE_AUTH_MODE=entra')
    msal = new PublicClientApplication({
      auth: {
        clientId,
        authority: `https://login.microsoftonline.com/${tenantId}`,
        redirectUri: window.location.origin,
      },
      cache: { cacheLocation: 'sessionStorage' },
    })
  }
  return msal
}

/** Entra mode: initialises MSAL, completes a pending redirect and starts a sign-in if nobody is signed in. Dev mode: no-op. */
export async function initAuth(): Promise<void> {
  if (getAuthMode() !== 'entra') return
  const app = getMsal()
  await app.initialize()
  const result = await app.handleRedirectPromise()
  if (result?.account) app.setActiveAccount(result.account)
  const account = activeAccount(app)
  if (!account) await app.loginRedirect({ scopes: scopes() })
}

function activeAccount(app: PublicClientApplication): AccountInfo | null {
  return app.getActiveAccount() ?? app.getAllAccounts()[0] ?? null
}

/** The bearer token for the API, or null in dev mode (the server needs none). */
export async function getAccessToken(): Promise<string | null> {
  if (getAuthMode() !== 'entra') return null
  const app = getMsal()
  const account = activeAccount(app)
  if (!account) {
    await app.loginRedirect({ scopes: scopes() })
    return null
  }
  try {
    const result = await app.acquireTokenSilent({ scopes: scopes(), account })
    return result.accessToken
  } catch (e) {
    if (e instanceof InteractionRequiredAuthError) {
      await app.acquireTokenRedirect({ scopes: scopes(), account })
      return null
    }
    throw e
  }
}
