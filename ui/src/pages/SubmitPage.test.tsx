import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import SubmitPage from './SubmitPage'

const ORG = { id: '11111111-1111-1111-1111-111111111111', name: 'Acme', retentionDays: 30 }

let submitCalls = 0
const server = setupServer(http.get('/api/orgs', () => HttpResponse.json([ORG])))
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  submitCalls = 0
})
afterAll(() => server.close())

function countSubmits(response: () => Response) {
  server.use(
    http.post('/api/tasks', () => {
      submitCalls++
      return response()
    }),
  )
}

async function renderPage() {
  render(
    <MemoryRouter initialEntries={['/submit']}>
      <Routes>
        <Route path="/submit" element={<SubmitPage />} />
        <Route path="/tasks/:id" element={<div>task page placeholder</div>} />
      </Routes>
    </MemoryRouter>,
  )
  // wait for the org list to load
  await screen.findByRole('option', { name: 'Acme' })
}

const submit = () => userEvent.click(screen.getByRole('button', { name: /submit/i }))

describe('SubmitPage', () => {
  it('RequiresOrg', async () => {
    countSubmits(() => HttpResponse.json({}, { status: 201 }))
    await renderPage()
    await userEvent.type(screen.getByLabelText(/url/i), 'https://example.com/a')
    await submit()

    expect(await screen.findByText(/select an organization/i)).toBeInTheDocument()
    expect(submitCalls).toBe(0)
  })

  it('RequiresExactlyOneOfFileOrUrl', async () => {
    countSubmits(() => HttpResponse.json({}, { status: 201 }))
    await renderPage()
    await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)

    // neither
    await submit()
    expect(await screen.findByText(/provide a file or a url/i)).toBeInTheDocument()

    // both
    await userEvent.type(screen.getByLabelText(/url/i), 'https://example.com/a')
    await userEvent.upload(screen.getByLabelText(/file/i), new File(['MZ'], 'a.exe'))
    await submit()
    expect(await screen.findByText(/either a file or a url, not both/i)).toBeInTheDocument()
    expect(submitCalls).toBe(0)
  })

  it('RejectsNonHttpUrl', async () => {
    countSubmits(() => HttpResponse.json({}, { status: 201 }))
    await renderPage()
    await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
    await userEvent.type(screen.getByLabelText(/url/i), 'javascript:alert(1)')
    await submit()

    expect(await screen.findByText(/http or https/i)).toBeInTheDocument()
    expect(submitCalls).toBe(0)
  })

  it('ShowsServerValidationErrors', async () => {
    countSubmits(() =>
      HttpResponse.json(
        { title: 'One or more validation errors occurred.', status: 400, errors: { url: ['server says no to this url'], durationSec: ['durationSec must be an integer between 60 and 1800.'] } },
        { status: 400 },
      ),
    )
    await renderPage()
    await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
    await userEvent.type(screen.getByLabelText(/url/i), 'https://example.com/a')
    await submit()

    expect(await screen.findByText('server says no to this url')).toBeInTheDocument()
    expect(screen.getByText(/durationSec must be an integer/)).toBeInTheDocument()
    expect(submitCalls).toBe(1)
  })

  it('OnSuccess_NavigatesToTaskPage', async () => {
    let form: FormData | undefined
    server.use(
      http.post('/api/tasks', async ({ request }) => {
        submitCalls++
        form = await request.formData()
        return HttpResponse.json({ id: '22222222-2222-2222-2222-222222222222' }, { status: 201 })
      }),
    )
    await renderPage()
    await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
    await userEvent.type(screen.getByLabelText(/url/i), 'https://example.com/a')
    await submit()

    await waitFor(() => expect(screen.getByText('task page placeholder')).toBeInTheDocument())
    expect(form?.get('orgId')).toBe(ORG.id)
    expect(form?.get('url')).toBe('https://example.com/a')
    expect(form?.get('networkMode')).toBe('Simulated')
    expect(form?.get('durationSec')).toBe('300')
    expect(form?.get('interactive')).toBe('true')
    expect(form?.has('file')).toBe(false)
  })

  it('uploads the chosen file as multipart (no url field)', async () => {
    let form: FormData | undefined
    server.use(
      http.post('/api/tasks', async ({ request }) => {
        form = await request.formData()
        return HttpResponse.json({ id: '33333333-3333-3333-3333-333333333333' }, { status: 201 })
      }),
    )
    await renderPage()
    await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
    await userEvent.upload(screen.getByLabelText(/file/i), new File(['MZ'], 'evil.exe'))
    await submit()

    await waitFor(() => expect(screen.getByText('task page placeholder')).toBeInTheDocument())
    const sent = form?.get('file')
    // (jsdom's File is serialised by Node's fetch in tests, so the filename is not preserved here; real browsers keep it)
    expect(typeof sent).not.toBe('string')
    expect(await (sent as Blob).text()).toBe('MZ')
    expect(form?.has('url')).toBe(false)
  })
})
