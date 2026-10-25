import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import { useShortcuts } from './useShortcuts'

function Host({ map }: { map: Record<string, () => void> }) {
  useShortcuts(map)
  return <><input aria-label="field" /><textarea aria-label="area" /></>
}

afterEach(() => vi.useRealTimers())

it('runs g t but ignores keys typed in an input', async () => {
  const user = userEvent.setup()
  const goT = vi.fn()
  const slash = vi.fn()
  render(<Host map={{ 'g t': goT, '/': slash }} />)
  await user.keyboard('gt')
  expect(goT).toHaveBeenCalledTimes(1)
  await user.click(screen.getByLabelText('field'))
  await user.keyboard('gt')
  expect(goT).toHaveBeenCalledTimes(1)
  await user.click(screen.getByLabelText('area'))
  await user.keyboard('/')
  expect(slash).not.toHaveBeenCalled()
  await user.click(document.body)
  await user.keyboard('/')
  expect(slash).toHaveBeenCalledTimes(1)
})

it('drops the sequence when the second key is late', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
  const goT = vi.fn()
  render(<Host map={{ 'g t': goT }} />)
  await user.keyboard('g')
  vi.advanceTimersByTime(1100)
  await user.keyboard('t')
  expect(goT).not.toHaveBeenCalled()
})

it('ignores shortcuts while a modal dialog is open but still runs Escape', async () => {
  const user = userEvent.setup()
  const q = vi.fn()
  const esc = vi.fn()
  render(<><div aria-modal="true" /><Host map={{ '?': q, Escape: esc }} /></>)
  await user.keyboard('?')
  expect(q).not.toHaveBeenCalled()
  await user.keyboard('{Escape}')
  expect(esc).toHaveBeenCalledTimes(1)
})
