import { useCallback } from 'react'
import { downloadFile } from '../api/client'
import { messageOf } from '../util/apiMessage'
import { useToast } from './useToast'

/** Returns `download(path, fileName)`: an authenticated download whose failure is shown as an error toast. */
export function useDownload(): (path: string, fileName: string) => Promise<void> {
  const toast = useToast()
  return useCallback(async (path: string, fileName: string) => {
    try {
      await downloadFile(path, fileName)
    } catch (e) {
      toast.error(messageOf(e).join(' '))
    }
  }, [toast])
}
