import { createContext, useContext } from 'react'

export type ToastApi = { success(text: string): void; error(text: string): void }

/** Provided by ToastProvider (Toaster.tsx). */
export const ToastContext = createContext<ToastApi | null>(null)

export function useToast(): ToastApi {
  const api = useContext(ToastContext)
  if (!api) throw new Error('useToast must be used inside ToastProvider')
  return api
}
