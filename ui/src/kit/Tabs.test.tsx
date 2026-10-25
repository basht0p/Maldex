import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { Tabs } from './Tabs'

const tabs = [
  { id: 'a', label: 'Alpha' },
  { id: 'b', label: 'Beta' },
  { id: 'c', label: 'Gamma' },
]

function Search() {
  return <output data-testid="search">{useLocation().search}</output>
}

function setup(url = '/') {
  render(
    <MemoryRouter initialEntries={[url]}>
      <Tabs tabs={tabs} label="Sections">{(active) => <p>panel {active}</p>}</Tabs>
      <Search />
    </MemoryRouter>,
  )
}

describe('Tabs', () => {
  it('selects the first tab for a missing or unknown value', () => {
    setup('/?tab=nope')
    expect(screen.getByRole('tab', { name: 'Alpha' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByText('panel a')).toBeInTheDocument()
  })

  it('moves focus and selection with the keyboard', async () => {
    const user = userEvent.setup()
    setup()
    await user.tab()
    expect(screen.getByRole('tab', { name: 'Alpha' })).toHaveFocus()
    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('tab', { name: 'Beta' })).toHaveFocus()
    expect(screen.getByRole('tab', { name: 'Beta' })).toHaveAttribute('aria-selected', 'true')
    await user.keyboard('{End}')
    expect(screen.getByRole('tab', { name: 'Gamma' })).toHaveFocus()
    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('tab', { name: 'Alpha' })).toHaveFocus()
    await user.keyboard('{ArrowLeft}')
    expect(screen.getByRole('tab', { name: 'Gamma' })).toHaveFocus()
    await user.keyboard('{Home}')
    expect(screen.getByRole('tab', { name: 'Alpha' })).toHaveFocus()
  })

  it('writes ?tab=<id> on selection', async () => {
    const user = userEvent.setup()
    setup()
    await user.click(screen.getByRole('tab', { name: 'Beta' }))
    expect(screen.getByTestId('search')).toHaveTextContent('?tab=b')
    expect(screen.getByText('panel b')).toBeInTheDocument()
  })
})
