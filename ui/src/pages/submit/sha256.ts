export const MAX_HASH_BYTES = 512 * 1024 ** 2

/** SHA-256 of a file as lowercase hex, or null when hashing is unavailable or the file is over 512 MiB. */
export async function sha256Hex(file: File): Promise<string | null> {
  try {
    if (file.size > MAX_HASH_BYTES) return null
    const subtle = globalThis.crypto?.subtle
    if (!subtle) return null
    const buf = await file.arrayBuffer()
    const digest = await subtle.digest('SHA-256', buf)
    return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('')
  } catch {
    return null
  }
}
