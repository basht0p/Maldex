import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { EmptyState } from './EmptyState'
import { Skeleton } from './Skeleton'

describe('Skeleton and EmptyState', () => {
  it('Skeleton is busy with a Loading label and N rows', () => {
    const { container } = render(<Skeleton rows={4} />)
    const el = screen.getByLabelText('Loading')
    expect(el).toHaveAttribute('aria-busy', 'true')
    expect(container.querySelectorAll('[data-skeleton-row]')).toHaveLength(4)
  })
  it('Skeleton defaults to 3 rows', () => {
    const { container } = render(<Skeleton />)
    expect(container.querySelectorAll('[data-skeleton-row]')).toHaveLength(3)
  })
  it('EmptyState shows title, body and action', () => {
    render(<EmptyState title="No tasks" body="Submit a sample." action={<button>Submit</button>} />)
    expect(screen.getByText('No tasks')).toBeInTheDocument()
    expect(screen.getByText('Submit a sample.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Submit' })).toBeInTheDocument()
  })
})
