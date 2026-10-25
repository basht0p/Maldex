import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import { renderWithKit } from '../test/renderWithKit'
import OrgsAdminPage from './OrgsAdminPage'

const server = setupServer(http.get('/api/orgs', () => HttpResponse.json([{ id: 'o1', name: 'Acme', retentionDays: 30 }])))
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

describe('OrgsAdminPage', () => {
  it('scrolls the organizations table inside its panel', async () => {
    renderWithKit(<OrgsAdminPage me={{ id: 'u1', displayName: 'Dev', role: 'Admin', maxUploadBytes: 1024 }} />)
    await screen.findByLabelText('Name of Acme')
    expect(screen.getByRole('table').parentElement?.className).toMatch(/tableScroll/)
  })
})
