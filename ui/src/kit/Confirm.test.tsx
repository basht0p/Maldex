import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it, vi } from 'vitest'
import { ConfirmProvider, useConfirm } from './Confirm'

function Opener({ onResult }: { onResult: (ok: boolean) => void }) {
  const confirm = useConfirm()
  return <button onClick={async () => onResult(await confirm({ title: 'Stop this task?', confirmLabel: 'Stop', danger: true }))}>open</button>
}

it('resolves true on confirm and false on Escape, and restores focus', async () => {
  const user = userEvent.setup()
  const onResult = vi.fn()
  render(<ConfirmProvider><Opener onResult={onResult} /></ConfirmProvider>)
  const opener = screen.getByText('open')
  await user.click(opener)
  const dialog = screen.getByRole('dialog', { name: 'Stop this task?' })
  expect(dialog).toContainElement(document.activeElement as HTMLElement)
  expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus()
  await user.click(screen.getByRole('button', { name: 'Stop' }))
  await waitFor(() => expect(onResult).toHaveBeenLastCalledWith(true))
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

  await user.click(opener)
  await screen.findByRole('dialog')
  await user.keyboard('{Escape}')
  await waitFor(() => expect(onResult).toHaveBeenLastCalledWith(false))
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  await waitFor(() => expect(opener).toHaveFocus())
})

it('traps Tab inside the dialog', async () => {
  const user = userEvent.setup()
  render(<ConfirmProvider><Opener onResult={() => {}} /></ConfirmProvider>)
  await user.click(screen.getByText('open'))
  await screen.findByRole('dialog')
  await user.tab()
  expect(screen.getByRole('button', { name: 'Stop' })).toHaveFocus()
  await user.tab()
  expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus()
})
