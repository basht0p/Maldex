import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { SortHeader } from './SortHeader'

function setup(sort: string, dir: 'asc' | 'desc', onSort = vi.fn()) {
  render(
    <table>
      <thead>
        <tr><SortHeader field="name" label="Name" sort={sort} dir={dir} onSort={onSort} /></tr>
      </thead>
    </table>,
  )
  return onSort
}

describe('SortHeader', () => {
  it('reports aria-sort', () => {
    setup('name', 'desc')
    expect(screen.getByRole('columnheader')).toHaveAttribute('aria-sort', 'descending')
  })

  it('is none when inactive', () => {
    setup('other', 'desc')
    expect(screen.getByRole('columnheader')).toHaveAttribute('aria-sort', 'none')
  })

  it('flips direction when active', async () => {
    const onSort = setup('name', 'desc')
    await userEvent.click(screen.getByRole('button', { name: /Name/ }))
    expect(onSort).toHaveBeenCalledWith('name', 'asc')
  })

  it('sorts desc first when inactive', async () => {
    const onSort = setup('other', 'asc')
    await userEvent.click(screen.getByRole('button', { name: /Name/ }))
    expect(onSort).toHaveBeenCalledWith('name', 'desc')
  })
})
