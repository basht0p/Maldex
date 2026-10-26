/** Hooks the API client calls on session-level failures; App wires them to routing and toasts. */
type Handler = () => void
const noop: Handler = () => undefined

let unauthorized: Handler = noop
let noRole: Handler = noop
let blocked: Handler = noop

function register(set: (h: Handler) => void, get: () => Handler, handler: Handler): () => void {
  set(handler)
  return () => { if (get() === handler) set(noop) }
}

/** Called on any 401 outside /api/auth/* (not signed in, or the session expired). Returns an unsubscribe. */
export const onUnauthorized = (handler: Handler) => register((h) => { unauthorized = h }, () => unauthorized, handler)
/** Called on 403 { error: "no_role" }. */
export const onNoRole = (handler: Handler) => register((h) => { noRole = h }, () => noRole, handler)
/** Called on 403 { error: "csrf" | "origin" }. */
export const onBlocked = (handler: Handler) => register((h) => { blocked = h }, () => blocked, handler)

export const notifyUnauthorized = () => unauthorized()
export const notifyNoRole = () => noRole()
export const notifyBlocked = () => blocked()

/** Full-page navigations, behind an object so tests can replace them. */
export const nav = {
  assign: (url: string) => window.location.assign(url),
}

/** `raw` only when it is a same-origin path (starts with `/`, not `//` and not `/\`); otherwise `/`. */
export function safeReturnUrl(raw: string | null): string {
  // eslint-disable-next-line no-control-regex
  if (raw && !/[\x00-\x1f\x7f]/.test(raw) && raw.startsWith('/') && !raw.startsWith('//') && !raw.startsWith('/\\')) return raw
  return '/'
}
