import { render } from '@testing-library/react'
import type { ReactElement, ReactNode } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { ConfirmProvider } from '../kit/Confirm'
import { ToastProvider } from '../kit/Toaster'

/** Renders `ui` inside the kit providers and a MemoryRouter. `rerender` keeps the same wrapper. */
export function renderWithKit(ui: ReactElement, { route = '/' }: { route?: string } = {}) {
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <ToastProvider>
      <ConfirmProvider>
        <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>
      </ConfirmProvider>
    </ToastProvider>
  )
  return render(ui, { wrapper: Wrapper })
}
