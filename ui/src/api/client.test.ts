import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import {
  ApiError, cancelTask, cancelTemplateJob, discardTemplateVersion, downloadFile, extendTask, fetchBlob, getMe, getTemplateJobTunnelToken, listTasks, publishTemplateVersion,
  sealTemplateJob, startTemplateEdit, startTemplateRefresh, stopTask,
} from './client'

vi.mock('../auth/auth', () => ({ getAccessToken: vi.fn(async () => null) }))
import { getAccessToken } from '../auth/auth'

const server = setupServer()
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

describe('client', () => {
  it('client_ThrowsApiErrorWithProblem', async () => {
    const problem = { title: 'Bad', status: 400, errors: { url: ['url must be http'] } }
    server.use(http.get('/api/tasks', () => HttpResponse.json(problem, { status: 400 })))

    const err = await listTasks().catch((e: unknown) => e)

    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(400)
    expect((err as ApiError).problem).toEqual(problem)
  })

  it('keeps a non-JSON error body as text', async () => {
    server.use(http.get('/api/me', () => new HttpResponse('boom', { status: 500 })))
    const err = (await getMe().catch((e: unknown) => e)) as ApiError
    expect(err.status).toBe(500)
    expect(err.problem).toBe('boom')
  })

  it('sends no Authorization header when there is no token (dev mode)', async () => {
    let auth: string | null = 'unset'
    server.use(
      http.get('/api/me', ({ request }) => {
        auth = request.headers.get('authorization')
        return HttpResponse.json({ id: 'u', displayName: 'Dev', role: 'Admin' })
      }),
    )
    const me = await getMe()
    expect(me.role).toBe('Admin')
    expect(auth).toBeNull()
  })

  it('attaches the bearer token when auth provides one (entra mode)', async () => {
    vi.mocked(getAccessToken).mockResolvedValueOnce('tok123')
    let auth: string | null = null
    server.use(
      http.get('/api/me', ({ request }) => {
        auth = request.headers.get('authorization')
        return HttpResponse.json({ id: 'u', displayName: 'A', role: 'Analyst' })
      }),
    )
    await getMe()
    expect(auth).toBe('Bearer tok123')
  })

  it('fetchBlob sends the bearer token and throws ApiError on a non-2xx response', async () => {
    vi.mocked(getAccessToken).mockResolvedValue('tok123')
    let auth: string | null = null
    server.use(
      http.get('/api/artifacts/a1/image', ({ request }) => {
        auth = request.headers.get('authorization')
        return new HttpResponse(new Uint8Array([0x89, 0x50]), { headers: { 'Content-Type': 'image/png' } })
      }),
      http.get('/api/artifacts/a2/image', () => HttpResponse.json({ title: 'Unauthorized' }, { status: 401 })),
    )
    const blob = await fetchBlob('/api/artifacts/a1/image')
    expect(blob.size).toBe(2)
    expect(auth).toBe('Bearer tok123')
    const err = (await fetchBlob('/api/artifacts/a2/image').catch((e: unknown) => e)) as ApiError
    expect(err).toBeInstanceOf(ApiError)
    expect(err.status).toBe(401)
    vi.mocked(getAccessToken).mockResolvedValue(null)
  })

  it('downloadFile saves the authenticated blob under the given name and revokes the URL', async () => {
    vi.mocked(getAccessToken).mockResolvedValueOnce('tok123')
    let auth: string | null = null
    server.use(http.get('/api/tasks/t1/report.pdf', ({ request }) => {
      auth = request.headers.get('authorization')
      return new HttpResponse('%PDF', { headers: { 'Content-Type': 'application/pdf' } })
    }))
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:x/1')
    const revoke = vi.spyOn(URL, 'revokeObjectURL')
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    await downloadFile('/api/tasks/t1/report.pdf', 'maldex-t1.pdf')
    expect(auth).toBe('Bearer tok123')
    const a = click.mock.contexts[0] as HTMLAnchorElement
    expect(create).toHaveBeenCalledOnce()
    expect(a.download).toBe('maldex-t1.pdf')
    expect(a.getAttribute('href')).toBe('blob:x/1')
    expect(a.isConnected).toBe(false)
    await vi.waitFor(() => expect(revoke).toHaveBeenCalledWith('blob:x/1'))
    click.mockRestore()
    create.mockRestore()
    revoke.mockRestore()
  })

  it('builds the task list query string', async () => {
    let search = ''
    server.use(
      http.get('/api/tasks', ({ request }) => {
        search = new URL(request.url).search
        return HttpResponse.json({ items: [], total: 0 })
      }),
    )
    await listTasks({ orgId: 'o1', status: 'Queued', skip: 50, take: 25 })
    const q = new URLSearchParams(search)
    expect(q.get('orgId')).toBe('o1')
    expect(q.get('status')).toBe('Queued')
    expect(q.get('skip')).toBe('50')
    expect(q.get('take')).toBe('25')
  })

  it('serializes the search, filter and sort fields and leaves out empty ones', async () => {
    let search = ''
    server.use(
      http.get('/api/tasks', ({ request }) => {
        search = new URL(request.url).search
        return HttpResponse.json({ items: [], total: 0 })
      }),
    )
    await listTasks({ q: 'a b', verdict: 'Malicious', mine: true, from: '2026-01-02', to: '2026-01-03', sort: 'duration', dir: 'asc' })
    const q = new URLSearchParams(search)
    expect([...q.entries()].sort()).toEqual([
      ['dir', 'asc'], ['from', '2026-01-02'], ['mine', 'true'], ['q', 'a b'], ['sort', 'duration'], ['to', '2026-01-03'], ['verdict', 'Malicious'],
    ])
    await listTasks({ q: '', mine: false })
    expect(search).toBe('')
  })

  it('extend posts {seconds}; stop tolerates an empty 202', async () => {
    let body: unknown
    server.use(
      http.post('/api/tasks/t1/extend', async ({ request }) => {
        body = await request.json()
        return HttpResponse.json({ id: 't1' })
      }),
      http.post('/api/tasks/t1/stop', () => new HttpResponse(null, { status: 202 })),
    )
    await extendTask('t1', 120)
    expect(body).toEqual({ seconds: 120 })
    await expect(stopTask('t1')).resolves.toBeUndefined()
  })

  it('encodes ids in the path', async () => {
    let url = ''
    server.use(http.post('*/api/tasks/*/cancel', ({ request }) => { url = new URL(request.url).pathname; return new HttpResponse(null, { status: 204 }) }))
    await cancelTask('a/b?c#d')
    expect(url).toBe('/api/tasks/a%2Fb%3Fc%23d/cancel')
  })

  describe('template jobs', () => {
    const captured = async (method: 'post' | 'get', path: string, call: () => Promise<unknown>) => {
      let body: unknown = 'unset'
      let hit = ''
      server.use(
        http[method](path, async ({ request }) => {
          hit = new URL(request.url).pathname
          body = request.method === 'POST' ? await request.json().catch(() => undefined) : undefined
          return HttpResponse.json({}, { status: 202 })
        }),
      )
      await call()
      return { body, hit }
    }

    it('Edit posts the network and publish flag', async () => {
      const r = await captured('post', '/api/admin/templates/win11-golden/edit', () => startTemplateEdit('win11-golden', 'Direct', false))
      expect(r.body).toEqual({ network: 'Direct', publishOnSuccess: false })
    })

    it('Refresh posts only the publish flag', async () => {
      const r = await captured('post', '/api/admin/templates/win11-golden/refresh', () => startTemplateRefresh('win11-golden', true))
      expect(r.body).toEqual({ publishOnSuccess: true })
    })

    it('Seal posts the note, or null when empty', async () => {
      expect((await captured('post', '/api/admin/template-jobs/j1/seal', () => sealTemplateJob('j1', 'Installed 7-Zip'))).body).toEqual({ note: 'Installed 7-Zip' })
      expect((await captured('post', '/api/admin/template-jobs/j1/seal', () => sealTemplateJob('j1', ''))).body).toEqual({ note: null })
    })

    it('Cancel, publish, discard and the token use the contract routes', async () => {
      expect((await captured('post', '/api/admin/template-jobs/j1/cancel', () => cancelTemplateJob('j1'))).hit).toBe('/api/admin/template-jobs/j1/cancel')
      expect((await captured('post', '/api/admin/templates/t/versions/v1/publish', () => publishTemplateVersion('t', 'v1'))).hit).toBe('/api/admin/templates/t/versions/v1/publish')
      expect((await captured('post', '/api/admin/templates/t/versions/v1/discard', () => discardTemplateVersion('t', 'v1'))).hit).toBe('/api/admin/templates/t/versions/v1/discard')
      expect((await captured('get', '/api/admin/template-jobs/j1/tunnel-token', () => getTemplateJobTunnelToken('j1'))).hit).toBe('/api/admin/template-jobs/j1/tunnel-token')
    })
  })
})
