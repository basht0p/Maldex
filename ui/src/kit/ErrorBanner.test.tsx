import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ErrorBanner } from './ErrorBanner'

describe('ErrorBanner', () => {
  it('shows the message as an alert and retries', async () => {
    const onRetry = vi.fn()
    render(<ErrorBanner message="Could not load tasks. Try again." onRetry={onRetry} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Could not load tasks. Try again.')
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(onRetry).toHaveBeenCalledTimes(1)
  })
  it('has no Retry button without onRetry', () => {
    render(<ErrorBanner message="Nope" />)
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull()
  })
})
