import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { HOSTILE, reportFixture } from '../../test/reportFixture'
import { ProcessesTab } from './ProcessesTab'

const procs = [
  { id: 1, parentId: null, pid: 100, imagePath: 'C:\\Windows\\evil.exe', commandLine: `evil.exe ${HOSTILE}`, user: 'bob', startedAt: '2026-10-19T00:00:11Z', endedAt: null, depth: 0 },
  { id: 2, parentId: 1, pid: 200, imagePath: 'C:\\Windows\\cmd.exe', commandLine: 'cmd /c whoami', user: 'bob', startedAt: '2026-10-19T00:00:12Z', endedAt: null, depth: 1 },
]

describe('ProcessesTab', () => {
  it('process row shows events for that process', async () => {
    const user = userEvent.setup()
    const onShow = vi.fn()
    render(<ProcessesTab report={reportFixture({ processes: procs })} onShowEvents={onShow} />)
    expect(screen.getByTitle('C:\\Windows\\evil.exe')).toHaveTextContent('evil.exe')
    await user.click(screen.getAllByRole('button', { name: 'Show events' })[0])
    expect(onShow).toHaveBeenCalledWith(1)
  })

  it('process command line is text, not markup', async () => {
    const user = userEvent.setup()
    const { container } = render(<ProcessesTab report={reportFixture({ processes: procs })} onShowEvents={() => {}} />)
    await user.click(screen.getByRole('button', { name: 'Expand evil.exe 100' }))
    expect(container.querySelector('pre')?.textContent).toContain(HOSTILE)
    expect(container.querySelector('script')).toBeNull()
  })

  it('shows the truncation notice and indents by depth', () => {
    const r = reportFixture({ processes: procs, truncated: { processes: true, networkFlows: false, dns: false, http: false } })
    const { container } = render(<ProcessesTab report={r} onShowEvents={() => {}} />)
    expect(screen.getByText(/Showing the first 2,000/)).toBeInTheDocument()
    expect((container.querySelectorAll('li')[1] as HTMLElement).style.paddingLeft).toBe('1.25rem')
  })
})
