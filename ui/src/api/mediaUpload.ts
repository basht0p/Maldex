import { ApiError, getMedia, putMediaChunk, startMediaUpload } from './client'
import type { MediaFileDto, MediaKind } from './types'

/** Used when resuming: the row does not say what chunk size the upload started with (the server accepts up to 64 MiB). */
const RESUME_CHUNK_BYTES = 16 * 1024 * 1024
/** A network error is retried after these waits (1, 2 and 4 s); then the upload rejects and the UI offers Resume. */
const RETRY_WAITS_MS = [1000, 2000, 4000]
/** The server's offset answers (409) in a row that we follow before giving up. */
const MAX_OFFSET_CORRECTIONS = 5

function aborted(): Error {
  return new DOMException('The upload was aborted.', 'AbortError')
}

function wait(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(aborted())
    const timer = setTimeout(() => { signal.removeEventListener('abort', onAbort); resolve() }, ms)
    const onAbort = () => { clearTimeout(timer); reject(aborted()) }
    signal.addEventListener('abort', onAbort, { once: true })
  })
}

function serverOffset(e: unknown): number | null {
  if (!(e instanceof ApiError) || e.status !== 409) return null
  const p = e.problem as { receivedBytes?: unknown } | undefined
  return typeof p?.receivedBytes === 'number' ? p.receivedBytes : null
}

/**
 * Uploads `file` in chunks (resumable). Without `resumeId` it creates the upload; with it, it reads the row and starts at
 * its `receivedBytes`. A 409 moves the offset to the server's `receivedBytes`; a network error is retried 3 times and then
 * rejects (the row stays Receiving, so the library offers Resume). Any other refusal rejects at once.
 */
export async function uploadMedia(
  file: File,
  kind: MediaKind,
  onProgress: (received: number, size: number) => void,
  signal: AbortSignal,
  resumeId?: string,
): Promise<MediaFileDto> {
  let id: string
  let offset: number
  let chunkBytes: number
  if (resumeId) {
    const row = await getMedia(resumeId)
    id = row.id
    offset = row.receivedBytes
    chunkBytes = RESUME_CHUNK_BYTES
  } else {
    const started = await startMediaUpload(file.name, file.size, kind)
    id = started.media.id
    offset = started.media.receivedBytes
    chunkBytes = started.chunkBytes
  }
  onProgress(offset, file.size)

  let corrections = 0
  while (offset < file.size) {
    if (signal.aborted) throw aborted()
    const chunk = file.slice(offset, Math.min(offset + chunkBytes, file.size))
    let attempt = 0
    for (;;) {
      try {
        const res = await putMediaChunk(id, offset, chunk, signal)
        offset = res.receivedBytes
        corrections = 0
        break
      } catch (e) {
        if (signal.aborted) throw aborted()
        const server = serverOffset(e)
        if (server !== null) {
          if (++corrections > MAX_OFFSET_CORRECTIONS) throw e
          offset = server
          break
        }
        if (e instanceof ApiError || attempt >= RETRY_WAITS_MS.length) throw e
        await wait(RETRY_WAITS_MS[attempt++], signal)
      }
    }
    onProgress(offset, file.size)
  }
  return getMedia(id)
}
