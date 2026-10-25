import { createContext, useContext } from 'react'

export type ConfirmOptions = { title: string; body?: string; confirmLabel: string; danger?: boolean }
export type Confirm = (o: ConfirmOptions) => Promise<boolean>

/** Provided by ConfirmProvider (Confirm.tsx). */
export const ConfirmContext = createContext<Confirm | null>(null)

export function useConfirm(): Confirm {
  const c = useContext(ConfirmContext)
  if (!c) throw new Error('useConfirm must be used inside ConfirmProvider')
  return c
}
