import { render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const guac = vi.hoisted(() => {
  const state = {
    tunnelUrls: [] as string[],
    connects: [] as string[],
    disconnects: 0,
    clients: [] as { onerror: ((s: { message?: string }) => void) | null }[],
  }
  class Client {
    onerror: ((s: { message?: string }) => void) | null = null
    onstatechange: ((s: number) => void) | null = null
    private el = document.createElement('div')
    constructor(_tunnel: unknown) {
      this.el.setAttribute('data-testid', 'guac-display')
      state.clients.push(this)
    }
    getDisplay() {
      return { getElement: () => this.el, getWidth: () => 1280, getHeight: () => 800, scale: vi.fn(), onresize: null }
    }
    connect(data: string) { state.connects.push(data) }
    disconnect() { state.disconnects++ }
    sendMouseState = vi.fn()
    sendKeyEvent = vi.fn()
  }
  class WebSocketTunnel { constructor(url: string) { state.tunnelUrls.push(url) } }
  class Mouse { constructor(_el: Element) {} onEach() {} }
  class Keyboard { constructor(_el: Element) {} reset() {} }
  return { state, default: { Client, WebSocketTunnel, Mouse, Keyboard } }
})
vi.mock('guacamole-common-js', () => ({ default: guac.default }))

const getTunnelToken = vi.fn()
const listArtifacts = vi.fn()
vi.mock('../api/client', () => ({
  getTunnelToken: (...a: unknown[]) => getTunnelToken(...a),
  listArtifacts: (...a: unknown[]) => listArtifacts(...a),
}))

import { SessionPane } from './SessionPane'

beforeEach(() => {
  vi.clearAllMocks()
  guac.state.tunnelUrls.length = 0
  guac.state.connects.length = 0
  guac.state.clients.length = 0
  guac.state.disconnects = 0
  getTunnelToken.mockResolvedValue({ token: 'tok-1' })
  listArtifacts.mockResolvedValue([])
})

describe('SessionPane', () => {
  it('SessionPane_RendersDisplay_WhenRunning', async () => {
    const { unmount } = render(<SessionPane taskId="t1" status="Running" />)
    expect(await screen.findByTestId('guac-display')).toBeInTheDocument()
    expect(getTunnelToken).toHaveBeenCalledWith('t1')
    expect(guac.state.tunnelUrls[0]).toMatch(/^wss?:\/\/[^/]+\/api\/tasks\/t1\/tunnel$/)
    expect(guac.state.connects[0]).toContain('token=tok-1')
    expect(screen.queryByText(/available while the task is running/i)).not.toBeInTheDocument()

    unmount()
    expect(guac.state.disconnects).toBe(1)
  })

  it('SessionPane_Placeholder_WhenNotRunning', async () => {
    render(<SessionPane taskId="t1" status="Completed" />)
    expect(await screen.findByText(/available while the task is running/i)).toBeInTheDocument()
    expect(getTunnelToken).not.toHaveBeenCalled()
    expect(screen.queryByTestId('guac-display')).not.toBeInTheDocument()
  })

  it('disconnects and returns to the placeholder when the task leaves Running', async () => {
    const { rerender } = render(<SessionPane taskId="t1" status="Running" />)
    await screen.findByTestId('guac-display')
    rerender(<SessionPane taskId="t1" status="Collecting" />)
    await waitFor(() => expect(guac.state.disconnects).toBe(1))
    expect(screen.queryByTestId('guac-display')).not.toBeInTheDocument()
    expect(screen.getByText(/available while the task is running/i)).toBeInTheDocument()
  })

  it('shows an error when the token cannot be fetched', async () => {
    getTunnelToken.mockRejectedValue(new Error('409'))
    render(<SessionPane taskId="t1" status="Running" />)
    expect(await screen.findByRole('alert')).toHaveTextContent(/live view/i)
    expect(guac.state.tunnelUrls).toHaveLength(0)
  })

  it('shows a tunnel error from the client', async () => {
    render(<SessionPane taskId="t1" status="Running" />)
    await screen.findByTestId('guac-display')
    guac.state.clients[0].onerror?.({ message: 'boom' })
    expect(await screen.findByRole('alert')).toBeInTheDocument()
  })
})
