import { afterEach, describe, expect, it, vi } from 'vitest'
import { loadLastOptions, saveLastOptions } from './lastOptions'

afterEach(() => {
  vi.restoreAllMocks()
  localStorage.clear()
})

describe('lastOptions', () => {
  it('round trips', () => {
    const o = { orgId: 'o1', image: 'win11', networkMode: 'LiveVpn' as const, exitCountry: 'NL', durationSec: '600', interactive: false }
    saveLastOptions(o)
    expect(loadLastOptions()).toEqual(o)
  })

  it('returns {} when storage throws on read', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied')
    })
    expect(loadLastOptions()).toEqual({})
  })

  it('does not throw when storage throws on write', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('full')
    })
    expect(() => saveLastOptions({ orgId: '', image: '', networkMode: 'None', exitCountry: '', durationSec: '300', interactive: true })).not.toThrow()
  })

  it('returns {} for junk JSON and drops mistyped fields', () => {
    localStorage.setItem('maldex.submit.lastOptions', '{nope')
    expect(loadLastOptions()).toEqual({})
    localStorage.setItem('maldex.submit.lastOptions', '[1]')
    expect(loadLastOptions()).toEqual({})
    localStorage.setItem('maldex.submit.lastOptions', JSON.stringify({ orgId: 5, durationSec: '90', interactive: 'yes' }))
    expect(loadLastOptions()).toEqual({ durationSec: '90' })
  })
})
