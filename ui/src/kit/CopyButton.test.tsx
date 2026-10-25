import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { CopyButton } from './CopyButton'

afterEach(() => { vi.restoreAllMocks() })

describe('CopyButton', () => {
  it('copies the value and announces it', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    render(<CopyButton value="abc" label="Copy SHA-256" />)
    await userEvent.click(screen.getByRole('button', { name: 'Copy SHA-256' }))
    expect(writeText).toHaveBeenCalledWith('abc')
    expect(await screen.findByText('Copied')).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Copied')
  })
  it('defaults the label to Copy', () => {
    render(<CopyButton value="x" />)
    expect(screen.getByRole('button', { name: 'Copy' })).toBeInTheDocument()
  })
  it('falls back to execCommand when the clipboard API is missing', async () => {
    Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true })
    const exec = vi.fn().mockReturnValue(true)
    Object.defineProperty(document, 'execCommand', { value: exec, configurable: true })
    render(<CopyButton value="abc" />)
    await userEvent.click(screen.getByRole('button', { name: 'Copy' }))
    expect(exec).toHaveBeenCalledWith('copy')
    expect(await screen.findByText('Copied')).toBeInTheDocument()
  })
})
