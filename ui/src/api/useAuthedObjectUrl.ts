import { useEffect, useState } from 'react'
import { fetchBlob } from './client'

interface State { path: string | null; url: string | null; failed: boolean }

/**
 * An object URL for `path`, fetched with the bearer token (for `<img>`; Entra mode rejects cookie-less image requests).
 * The URL is revoked when `path` changes or the component unmounts; an in-flight fetch is aborted then.
 * `url` is null while loading and after a failure; `failed` is true once the fetch failed.
 */
export function useAuthedObjectUrl(path: string | null): { url: string | null; failed: boolean } {
  const [state, setState] = useState<State>({ path: null, url: null, failed: false })

  useEffect(() => {
    if (!path) return
    const ctrl = new AbortController()
    let url: string | null = null
    fetchBlob(path, ctrl.signal).then(
      (blob) => {
        if (ctrl.signal.aborted) return
        url = URL.createObjectURL(blob)
        setState({ path, url, failed: false })
      },
      () => {
        if (!ctrl.signal.aborted) setState({ path, url: null, failed: true })
      },
    )
    return () => {
      ctrl.abort()
      if (url) URL.revokeObjectURL(url)
    }
  }, [path])

  // A result for an earlier path is never shown.
  return state.path === path && path ? { url: state.url, failed: state.failed } : { url: null, failed: false }
}
