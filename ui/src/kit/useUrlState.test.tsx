import { renderHook, act } from '@testing-library/react'
import { MemoryRouter, useLocation, useNavigationType } from 'react-router-dom'
import type { ReactNode } from 'react'
import { describe, expect, it } from 'vitest'
import { useUrlState, type UrlField } from './useUrlState'

const schema = {
  verdict: { kind: 'enum', values: ['', 'Malicious', 'Clean'], default: '' },
  page: { kind: 'number', default: 0, min: 0 },
  mine: { kind: 'boolean', default: false },
  from: { kind: 'date', default: '' },
  q: { kind: 'string', default: '' },
} satisfies Record<string, UrlField>

function setup(url: string) {
  const wrapper = ({ children }: { children: ReactNode }) => <MemoryRouter initialEntries={[url]}>{children}</MemoryRouter>
  return renderHook(
    () => ({ state: useUrlState(schema), loc: useLocation(), nav: useNavigationType() }),
    { wrapper },
  )
}

describe('useUrlState', () => {
  it('parses, defaults and drops invalid values', () => {
    const { result } = setup('/?verdict=Bogus&page=-3&mine=yes&from=2026-13-40&q=abc')
    expect(result.current.state[0]).toEqual({ verdict: '', page: 0, mine: false, from: '', q: 'abc' })
  })

  it('parses valid values', () => {
    const { result } = setup('/?verdict=Clean&page=4&mine=true&from=2026-02-28')
    expect(result.current.state[0]).toEqual({ verdict: 'Clean', page: 4, mine: true, from: '2026-02-28', q: '' })
  })

  it('writes non-defaults only, with replace navigation', () => {
    const { result } = setup('/?q=old')
    const before = window.history.length
    act(() => result.current.state[1]({ verdict: 'Malicious', q: '' }))
    expect(result.current.loc.search).toBe('?verdict=Malicious')
    expect(result.current.nav).toBe('REPLACE')
    expect(window.history.length).toBe(before)
  })

  it('preserves keys the schema does not own', () => {
    const { result } = setup('/?tab=events&q=x')
    act(() => result.current.state[1]({ page: 2 }))
    const p = new URLSearchParams(result.current.loc.search)
    expect(p.get('tab')).toBe('events')
    expect(p.get('page')).toBe('2')
  })
})
