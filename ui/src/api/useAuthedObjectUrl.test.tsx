import { renderHook, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { useAuthedObjectUrl } from './useAuthedObjectUrl'

vi.mock('../auth/auth', () => ({ getAccessToken: vi.fn(async () => 'tok') }))

const server = setupServer()
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.restoreAllMocks()
})
afterAll(() => server.close())

describe('useAuthedObjectUrl', () => {
  it('fetches with the token, gives an object URL and revokes it on change and unmount', async () => {
    const auth: (string | null)[] = []
    server.use(http.get('/img/:id', ({ request }) => {
      auth.push(request.headers.get('authorization'))
      return new HttpResponse(new Uint8Array([1, 2, 3]))
    }))
    let n = 0
    vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:u/${++n}`)
    const revoke = vi.spyOn(URL, 'revokeObjectURL')

    const { result, rerender, unmount } = renderHook(({ path }) => useAuthedObjectUrl(path), { initialProps: { path: '/img/a' as string | null } })
    expect(result.current).toEqual({ url: null, failed: false })
    await waitFor(() => expect(result.current.url).toBe('blob:u/1'))
    expect(auth).toEqual(['Bearer tok'])

    rerender({ path: '/img/b' })
    expect(revoke).toHaveBeenCalledWith('blob:u/1')
    expect(result.current.url).toBeNull() // never the previous path's image
    await waitFor(() => expect(result.current.url).toBe('blob:u/2'))

    unmount()
    expect(revoke).toHaveBeenCalledWith('blob:u/2')
  })

  it('reports a failed fetch', async () => {
    server.use(http.get('/img/x', () => new HttpResponse(null, { status: 401 })))
    const { result } = renderHook(() => useAuthedObjectUrl('/img/x'))
    await waitFor(() => expect(result.current.failed).toBe(true))
    expect(result.current.url).toBeNull()
  })

  it('does nothing for a null path', () => {
    const { result } = renderHook(() => useAuthedObjectUrl(null))
    expect(result.current).toEqual({ url: null, failed: false })
  })
})
