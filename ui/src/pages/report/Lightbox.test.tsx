import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { reportFixture } from '../../test/reportFixture'
import { Lightbox } from './Lightbox'
import { ScreenshotsTab } from './ScreenshotsTab'

describe('Lightbox', () => {
  it('lightbox moves with arrow keys, wraps, and closes on Escape with focus restored', async () => {
    const user = userEvent.setup()
    render(<ScreenshotsTab report={reportFixture()} />)
    const thumb = screen.getByRole('button', { name: 'Open screenshot 1 of 6' })
    await user.click(thumb)
    expect(screen.getByRole('dialog', { name: 'Screenshot 1 of 6' })).toBeInTheDocument()
    await user.keyboard('{ArrowLeft}')
    expect(screen.getByRole('dialog', { name: 'Screenshot 6 of 6' })).toBeInTheDocument()
    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('dialog', { name: 'Screenshot 1 of 6' })).toBeInTheDocument()
    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('dialog', { name: 'Screenshot 2 of 6' })).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(thumb).toHaveFocus()
  })

  it('works standalone without an index callback', async () => {
    const user = userEvent.setup()
    render(<Lightbox items={[{ src: '/a', caption: 'a' }, { src: '/b', caption: 'b' }]} index={0} onClose={() => {}} />)
    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('dialog', { name: 'Screenshot 2 of 2' })).toBeInTheDocument()
  })

  it('shows an empty state with no screenshots', () => {
    render(<ScreenshotsTab report={reportFixture({ screenshots: [] })} />)
    expect(screen.getByText('No screenshots')).toBeInTheDocument()
  })
})
