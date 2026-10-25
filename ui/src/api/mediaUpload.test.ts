import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MediaFileDto } from './types'

const startMediaUpload = vi.fn()
const getMedia = vi.fn()
const putMediaChunk = vi.fn()
vi.mock('./client', async (orig) => ({
  ...(await orig<typeof import('./client')>()),
  startMediaUpload: (...a: unknown[]) => startMediaUpload(...a),
  getMedia: (...a: unknown[]) => getMedia(...a),
  putMediaChunk: (...a: unknown[]) => putMediaChunk(...a),
}))

import { ApiError } from './client'
import { uploadMedia } from './mediaUpload'

const media = (over: Partial<MediaFileDto> = {}): MediaFileDto => ({
  id: 'm1', kind: 'OsInstaller', fileName: 'win.iso', size: 10, receivedBytes: 0, source: 'Upload', status: 'Receiving',
  images: [], addedBy: 'dev', addedAt: '2026-10-25T10:00:00Z', usedBy: [], ...over,
})
const file = (n: number) => new File([new Uint8Array(n)], 'win.iso')
const ctl = () => new AbortController().signal

/** Every chunk sent, as [offset, length]. */
const sent = () => putMediaChunk.mock.calls.map((c) => [c[1] as number, (c[2] as Blob).size])

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers()
  startMediaUpload.mockResolvedValue({ media: media(), chunkBytes: 4 })
  getMedia.mockResolvedValue(media({ receivedBytes: 10, status: 'Ready' }))
  putMediaChunk.mockImplementation(async (_id: string, offset: number, chunk: Blob) => ({ receivedBytes: offset + chunk.size, status: 'Receiving' }))
})
afterEach(() => vi.useRealTimers())

describe('uploadMedia', () => {
  it('creates the upload and sends chunkBytes slices at their offsets, reporting progress', async () => {
    const progress: number[][] = []
    const result = await uploadMedia(file(10), 'OsInstaller', (r, s) => progress.push([r, s]), ctl())
    expect(startMediaUpload).toHaveBeenCalledWith('win.iso', 10, 'OsInstaller')
    expect(sent()).toEqual([[0, 4], [4, 4], [8, 2]])
    expect(progress).toEqual([[0, 10], [4, 10], [8, 10], [10, 10]])
    expect(result.status).toBe('Ready')
  })

  it('with a resume id it reads the row and starts at receivedBytes without creating', async () => {
    getMedia.mockResolvedValueOnce(media({ receivedBytes: 8 }))
    await uploadMedia(file(10), 'OsInstaller', () => undefined, ctl(), 'm1')
    expect(startMediaUpload).not.toHaveBeenCalled()
    expect(getMedia).toHaveBeenCalledWith('m1')
    expect(sent()).toEqual([[8, 2]])
  })

  it('refuses to resume with a file whose name or size differs from the row', async () => {
    getMedia.mockResolvedValue(media({ receivedBytes: 8 }))
    await expect(uploadMedia(new File([new Uint8Array(10)], 'other.iso'), 'OsInstaller', () => undefined, ctl(), 'm1')).rejects.toThrow(/same file/i)
    await expect(uploadMedia(file(9), 'OsInstaller', () => undefined, ctl(), 'm1')).rejects.toThrow(/same file/i)
    expect(putMediaChunk).not.toHaveBeenCalled()
  })

  it('follows the offset the server reports in a 409', async () => {
    putMediaChunk.mockRejectedValueOnce(new ApiError(409, { title: 'offset', receivedBytes: 6 }))
    await uploadMedia(file(10), 'OsInstaller', () => undefined, ctl())
    expect(sent()).toEqual([[0, 4], [6, 4]])
  })

  it('retries a network error 3 times after 1, 2 and 4 seconds, then rejects', async () => {
    putMediaChunk.mockRejectedValue(new TypeError('Failed to fetch'))
    const outcome = uploadMedia(file(10), 'OsInstaller', () => undefined, ctl()).then(() => 'ok', (e: unknown) => e)
    await vi.advanceTimersByTimeAsync(0)
    expect(putMediaChunk).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1000)
    expect(putMediaChunk).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(1999)
    expect(putMediaChunk).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(1)
    expect(putMediaChunk).toHaveBeenCalledTimes(3)
    await vi.advanceTimersByTimeAsync(4000)
    expect(putMediaChunk).toHaveBeenCalledTimes(4)
    expect(await outcome).toBeInstanceOf(TypeError)
    await vi.advanceTimersByTimeAsync(10000)
    expect(putMediaChunk).toHaveBeenCalledTimes(4)
  })

  it('a successful retry carries on from the same offset', async () => {
    putMediaChunk.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    const done = uploadMedia(file(10), 'OsInstaller', () => undefined, ctl())
    await vi.advanceTimersByTimeAsync(1000)
    await done
    expect(sent()).toEqual([[0, 4], [0, 4], [4, 4], [8, 2]])
  })

  it('rejects at once on a server refusal other than 409 (no retry)', async () => {
    putMediaChunk.mockRejectedValue(new ApiError(400, { title: 'the file is too large' }))
    await expect(uploadMedia(file(10), 'OsInstaller', () => undefined, ctl())).rejects.toThrow('the file is too large')
    expect(putMediaChunk).toHaveBeenCalledTimes(1)
  })

  it('stops when aborted', async () => {
    const abort = new AbortController()
    putMediaChunk.mockImplementationOnce(async (_id: string, offset: number, chunk: Blob) => {
      abort.abort()
      return { receivedBytes: offset + chunk.size, status: 'Receiving' }
    })
    await expect(uploadMedia(file(10), 'OsInstaller', () => undefined, abort.signal)).rejects.toThrow(/abort/i)
    expect(putMediaChunk).toHaveBeenCalledTimes(1)
  })
})
