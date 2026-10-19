import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { ProcessNodeDto } from '../api/types'
import { ProcessTree } from './ProcessTree'

const proc = (over: Partial<ProcessNodeDto>): ProcessNodeDto => ({
  id: 1, pid: 100, parentId: null, imagePath: 'C:\\Windows\\explorer.exe', commandLine: 'explorer.exe /factory',
  user: null, startedAt: '2026-10-19T00:00:00Z', endedAt: null, ...over,
})

describe('ProcessTree', () => {
  it('ProcessTree_NestsChildren', () => {
    render(
      <ProcessTree
        processes={[
          proc({ id: 1, pid: 100, imagePath: 'C:\\Windows\\explorer.exe' }),
          proc({ id: 2, pid: 200, parentId: 1, imagePath: 'C:\\Users\\a\\dropper.exe', commandLine: 'dropper.exe /s' }),
          proc({ id: 3, pid: 300, parentId: 2, imagePath: 'C:\\Windows\\cmd.exe', commandLine: 'cmd.exe /c whoami' }),
        ]}
      />,
    )
    const root = screen.getByText('explorer.exe').closest('li')!
    const child = within(root).getByText('dropper.exe').closest('li')!
    expect(root.parentElement!.closest('li')).toBeNull() // the root is not nested
    expect(child.parentElement!.closest('li')).toBe(root)
    const grandchild = within(child).getByText('cmd.exe').closest('li')!
    expect(grandchild.parentElement!.closest('li')).toBe(child)
    expect(within(grandchild).getByText(/300/)).toBeInTheDocument()
    expect(screen.getByTitle('cmd.exe /c whoami')).toBeInTheDocument()
  })

  it('ProcessTree_OrphanIsRoot', () => {
    render(
      <ProcessTree
        processes={[
          proc({ id: 1, pid: 100, parentId: null, imagePath: 'a.exe', commandLine: 'a.exe /x' }),
          proc({ id: 2, pid: 200, parentId: 999, imagePath: 'orphan.exe', commandLine: 'orphan.exe /y' }),
        ]}
      />,
    )
    expect(screen.getByText('orphan.exe').closest('li')!.parentElement!.closest('li')).toBeNull()
    expect(screen.getAllByRole('listitem')).toHaveLength(2)
  })

  it('ProcessTree_RendersScriptTagAsText', () => {
    const hostile = '<img src=x onerror=alert(1)>'
    const { container } = render(<ProcessTree processes={[proc({ commandLine: hostile })]} />)
    expect(screen.getByText(hostile)).toBeInTheDocument()
    expect(container.querySelector('img')).toBeNull()
    expect(screen.getByTitle(hostile)).toBeInTheDocument()
  })
})
