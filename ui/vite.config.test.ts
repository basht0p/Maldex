// @vitest-environment node
import { once } from 'node:events'
import http from 'node:http'
import type { AddressInfo } from 'node:net'
import os from 'node:os'
import { fileURLToPath } from 'node:url'
import { afterAll, beforeAll, expect, it } from 'vitest'
import { createServer, loadConfigFromFile } from 'vite'
import type { ProxyOptions, ViteDevServer } from 'vite'

// The dev loop (./build.sh run): the browser talks to Vite on :5173, which proxies /api, /hubs and /signin-oidc to the backend.
// The backend's CsrfMiddleware refuses any /api or /hubs request whose Origin is not {scheme}://{Host}, and browsers send
// Origin http://localhost:5173 on every POST/PUT/PATCH/DELETE and WebSocket upgrade. So the proxy must hand the backend the
// browser's Host. This runs the real proxy table from vite.config.ts through a real Vite dev server against an echo backend.

type Seen = { host?: string; origin?: string; upgrade: boolean }
const seen = new Map<string, Seen>()
let backend: http.Server
let vite: ViteDevServer
let port = 0

beforeAll(async () => {
  backend = http.createServer((req, res) => {
    seen.set(req.url ?? '', { host: req.headers.host, origin: req.headers.origin, upgrade: false })
    res.setHeader('Content-Type', 'application/json')
    res.end('{}')
  })
  backend.on('upgrade', (req, socket) => {
    seen.set(req.url ?? '', { host: req.headers.host, origin: req.headers.origin, upgrade: true })
    socket.end('HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n')
  })
  backend.listen(0, '127.0.0.1')
  await once(backend, 'listening')
  const target = `http://127.0.0.1:${(backend.address() as AddressInfo).port}`

  const loaded = await loadConfigFromFile({ command: 'serve', mode: 'development' }, fileURLToPath(new URL('./vite.config.ts', import.meta.url)))
  const table = (loaded?.config.server?.proxy ?? {}) as Record<string, ProxyOptions>
  const proxy = Object.fromEntries(Object.entries(table).map(([path, options]) => [path, { ...options, target }]))
  vite = await createServer({ configFile: false, root: os.tmpdir(), logLevel: 'silent', server: { host: '127.0.0.1', port: 0, proxy, hmr: false, watch: null } })
  await vite.listen()
  port = (vite.httpServer!.address() as AddressInfo).port
}, 30_000)

afterAll(async () => {
  await vite?.close()
  backend?.close()
})

/** What the backend's same-origin check compares: the Origin's host:port against the Host header. */
function sameOrigin(s: Seen | undefined) {
  return s?.origin !== undefined && s.host !== undefined && new URL(s.origin).host === s.host
}

function send(method: string, path: string, upgrade = false): Promise<void> {
  return new Promise((resolve) => {
    const headers: Record<string, string> = { Host: `localhost:${port}`, Origin: `http://localhost:${port}`, 'X-Maldex-Csrf': '1' }
    if (upgrade) Object.assign(headers, { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': 'dGhlIHNhbXBsZSBub25jZQ==' })
    const req = http.request({ host: '127.0.0.1', port, method, path, headers, agent: false }, (res) => { res.resume(); res.on('end', () => resolve()) })
    req.on('error', () => resolve()) // the echo backend refuses upgrades; only what it saw matters
    req.on('upgrade', (_res, socket) => { socket.destroy(); resolve() })
    req.end()
  })
}

it.each([
  ['POST', '/api/tasks/url'],
  ['DELETE', '/api/admin/orgs/1'],
  ['POST', '/api/auth/login'],
  ['POST', '/hubs/tasks/negotiate?negotiateVersion=1'],
  ['GET', '/signin-oidc?code=x'],
])('%s %s reaches the backend with the browser Host, so Origin matches', async (method, path) => {
  await send(method, path)
  expect(seen.get(path)?.upgrade).toBe(false)
  expect(seen.get(path)).toMatchObject({ host: `localhost:${port}`, origin: `http://localhost:${port}` })
  expect(sameOrigin(seen.get(path))).toBe(true)
})

it.each([
  '/hubs/tasks?id=abc',
  '/api/tasks/0b9f/tunnel?token=t',
])('the WebSocket upgrade of %s keeps the browser Host, so Origin matches', async (path) => {
  await send('GET', path, true)
  expect(seen.get(path)?.upgrade).toBe(true)
  expect(sameOrigin(seen.get(path))).toBe(true)
})
