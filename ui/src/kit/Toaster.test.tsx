import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import { ToastProvider } from './Toaster'
import { useToast } from './useToast'

function Buttons() {
  const toast = useToast()
  return (
    <>
      <button onClick={() => toast.success('Task cancelled')}>ok</button>
      <button onClick={() => toast.error('Cancel failed')}>fail</button>
    </>
  )
}

afterEach(() => vi.useRealTimers())

it('auto-dismisses success after 5 s but keeps errors', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
  render(<ToastProvider><Buttons /></ToastProvider>)
  await user.click(screen.getByText('ok'))
  await user.click(screen.getByText('fail'))
  const region = screen.getByRole('status')
  expect(within(region).getByText('Task cancelled')).toBeInTheDocument()
  expect(within(region).getByText('Cancel failed')).toBeInTheDocument()
  act(() => { vi.advanceTimersByTime(5_000) })
  expect(screen.queryByText('Task cancelled')).not.toBeInTheDocument()
  expect(screen.getByText('Cancel failed')).toBeInTheDocument()
  await user.click(screen.getByRole('button', { name: 'Dismiss' }))
  expect(screen.queryByText('Cancel failed')).not.toBeInTheDocument()
})
