import { configure, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { Route, Routes } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { renderWithKit } from '../test/renderWithKit'
import SubmitPage from './SubmitPage'

// Several tests chain four async waits; the 1 s default flakes under a loaded full suite.
configure({ asyncUtilTimeout: 5000 })
vi.setConfig({ testTimeout: 15000 })

const ORG = { id: '11111111-1111-1111-1111-111111111111', name: 'Acme', retentionDays: 30 }

let submitCalls = 0
const WIN11 = { id: 'win11-26h2', displayName: 'Windows 11 26H2', isDefault: true }
const GOLDEN = { id: 'win11-golden', displayName: 'Windows 11 Golden', isDefault: false }

// Set once the mocked /api/templates response has been sent to the page (not merely requested).
let templatesAnswered = false
const server = setupServer(
  http.get('/api/orgs', () => HttpResponse.json([ORG])),
  http.get('/api/templates', () => HttpResponse.json([WIN11])),
  http.get('/api/tasks', () => HttpResponse.json({ items: [], total: 0 })),
)
server.events.on('response:mocked', ({ request }) => {
  if (new URL(request.url).pathname === '/api/templates') templatesAnswered = true
})
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  submitCalls = 0
  templatesAnswered = false
  localStorage.clear()
  vi.unstubAllGlobals()
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

async function renderPage(route = '/submit', maxUploadBytes?: number) {
  renderWithKit(
    <Routes>
      <Route path="/submit" element={<SubmitPage maxUploadBytes={maxUploadBytes} />} />
      <Route path="/tasks/:id" element={<div>task page placeholder</div>} />
    </Routes>,
    { route },
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

  describe('template picker', () => {
    const twoTemplates = () => server.use(http.get('/api/templates', () => HttpResponse.json([WIN11, GOLDEN])))

    it('shows a Template select with the default selected when more than one template is enabled', async () => {
      twoTemplates()
      await renderPage()
      const select = await screen.findByLabelText('Template')
      expect(select).toHaveValue(WIN11.id)
      expect(screen.getByRole('option', { name: 'Windows 11 Golden' })).toBeInTheDocument()
    })

    it('sends the chosen template id as image', async () => {
      twoTemplates()
      let form: FormData | undefined
      server.use(
        http.post('/api/tasks', async ({ request }) => {
          form = await request.formData()
          return HttpResponse.json({ id: '44444444-4444-4444-4444-444444444444' }, { status: 201 })
        }),
      )
      await renderPage()
      await userEvent.selectOptions(await screen.findByLabelText('Template'), GOLDEN.id)
      await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
      await userEvent.type(screen.getByLabelText(/url/i), 'https://example.com/a')
      await submit()

      await waitFor(() => expect(screen.getByText('task page placeholder')).toBeInTheDocument())
      expect(form?.get('image')).toBe(GOLDEN.id)
    })

    it('sends the default template when the analyst does not change it', async () => {
      twoTemplates()
      let form: FormData | undefined
      server.use(
        http.post('/api/tasks', async ({ request }) => {
          form = await request.formData()
          return HttpResponse.json({ id: '55555555-5555-5555-5555-555555555555' }, { status: 201 })
        }),
      )
      await renderPage()
      await screen.findByLabelText('Template')
      await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
      await userEvent.type(screen.getByLabelText(/url/i), 'https://example.com/a')
      await submit()

      await waitFor(() => expect(screen.getByText('task page placeholder')).toBeInTheDocument())
      expect(form?.get('image')).toBe(WIN11.id)
    })

    it('hides the field and sends no image when only one template is enabled', async () => {
      let form: FormData | undefined
      server.use(
        http.post('/api/tasks', async ({ request }) => {
          form = await request.formData()
          return HttpResponse.json({ id: '66666666-6666-6666-6666-666666666666' }, { status: 201 })
        }),
      )
      await renderPage()
      await waitFor(() => expect(templatesAnswered).toBe(true))

      await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
      await userEvent.type(screen.getByLabelText(/url/i), 'https://example.com/a')
      expect(screen.queryByLabelText('Template')).toBeNull() // the list has long been applied by now
      await submit()
      await waitFor(() => expect(screen.getByText('task page placeholder')).toBeInTheDocument())
      expect(form?.has('image')).toBe(false)
    })

    it('shows a server error on image once, without a field prefix', async () => {
      twoTemplates()
      countSubmits(() =>
        HttpResponse.json({ title: 'One or more validation errors occurred.', status: 400, errors: { image: ['Template is disabled.'] } }, { status: 400 }),
      )
      await renderPage()
      await screen.findByLabelText('Template')
      await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
      await userEvent.type(screen.getByLabelText(/url/i), 'https://example.com/a')
      await submit()

      expect(await screen.findByText('Template is disabled.')).toBeInTheDocument()
      expect(screen.queryByText(/image:/)).toBeNull()
    })
  })

  describe('multiple files', () => {
    const pick = (...names: string[]) => userEvent.upload(screen.getByLabelText('File'), names.map((n) => new File([n], `${n}.bin`)))

    it('submits several dropped files as separate tasks and reports each', async () => {
      const bodies: string[] = []
      server.use(
        http.post('/api/tasks', async ({ request }) => {
          const f = (await request.formData()).get('file') as Blob
          bodies.push(await f.text())
          if (bodies.length === 2) return HttpResponse.json({ title: 'Sample rejected by the server.', status: 400 }, { status: 400 })
          return HttpResponse.json({ id: `task-${bodies.length}` }, { status: 201 })
        }),
      )
      await renderPage()
      await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
      await pick('a', 'b', 'c')
      await submit()

      expect(await screen.findByText('Failed: Sample rejected by the server.')).toBeInTheDocument()
      await waitFor(() => expect(screen.getAllByText('Created')).toHaveLength(2))
      expect(bodies).toEqual(['a', 'b', 'c'])
      expect(screen.getByText('Created 2 tasks')).toBeInTheDocument()
      expect(screen.getAllByRole('link', { name: 'Open task' })[0]).toHaveAttribute('href', '/tasks/task-1')
      expect(screen.queryByText('task page placeholder')).toBeNull()
    })

    it('disables Submit while a batch is running', async () => {
      let release: () => void = () => {}
      const gate = new Promise<void>((r) => (release = r))
      server.use(
        http.post('/api/tasks', async () => {
          await gate
          return HttpResponse.json({ id: 'task-1' }, { status: 201 })
        }),
      )
      await renderPage()
      await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
      await pick('a', 'b')
      await submit()

      await waitFor(() => expect(screen.getByRole('button', { name: /submit/i })).toBeDisabled())
      expect(await screen.findByText('Uploading')).toBeInTheDocument()
      expect(screen.getByText('Queued for upload')).toBeInTheDocument()
      release()
      await waitFor(() => expect(screen.getByRole('button', { name: /submit/i })).toBeEnabled())
    })

    it('removes a listed file', async () => {
      await renderPage()
      await pick('a', 'b')
      await userEvent.click(screen.getByRole('button', { name: 'Remove a.bin' }))
      expect(screen.queryByText('a.bin')).toBeNull()
      expect(screen.getByText('b.bin')).toBeInTheDocument()
    })
  })

  describe('remembered options', () => {
    it('prefills the last options and ignores a vanished template', async () => {
      localStorage.setItem(
        'maldex.submit.lastOptions',
        JSON.stringify({ orgId: ORG.id, image: 'gone-template', networkMode: 'None', exitCountry: '', durationSec: '120', interactive: false }),
      )
      server.use(http.get('/api/templates', () => HttpResponse.json([WIN11, GOLDEN])))
      await renderPage()
      await screen.findByLabelText('Template')
      expect(screen.getByLabelText(/organization/i)).toHaveValue(ORG.id)
      expect(screen.getByLabelText('Template')).toHaveValue(WIN11.id)
      expect(screen.getByLabelText(/network mode/i)).toHaveValue('None')
      expect(screen.getByLabelText(/duration/i)).toHaveValue(120)
      expect(screen.getByLabelText(/interactive/i)).not.toBeChecked()
      expect(screen.queryByRole('alert')).toBeNull()
    })

    it('falls back to no organization when the remembered one vanished', async () => {
      localStorage.setItem('maldex.submit.lastOptions', JSON.stringify({ orgId: 'someone-else' }))
      await renderPage()
      expect(screen.getByLabelText(/organization/i)).toHaveValue('')
    })

    it('saves the options after a successful submission', async () => {
      server.use(http.post('/api/tasks', () => HttpResponse.json({ id: 'task-1' }, { status: 201 })))
      await renderPage()
      await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
      await userEvent.selectOptions(screen.getByLabelText(/network mode/i), 'None')
      await userEvent.type(screen.getByLabelText(/url/i), 'https://example.com/a')
      await submit()
      await waitFor(() => expect(screen.getByText('task page placeholder')).toBeInTheDocument())
      expect(JSON.parse(localStorage.getItem('maldex.submit.lastOptions') ?? '{}')).toMatchObject({ orgId: ORG.id, networkMode: 'None', durationSec: '300', interactive: true })
    })
  })

  describe('duplicate notice', () => {
    const stubDigest = () => {
      const digest = vi.fn().mockResolvedValue(new Uint8Array(32).fill(0xab).buffer)
      vi.stubGlobal('crypto', { subtle: { digest } })
      return digest
    }
    const withBuffer = (name: string, size?: number) => {
      const f = new File(['MZ'], name)
      f.arrayBuffer = () => Promise.resolve(new TextEncoder().encode('MZ').buffer as ArrayBuffer)
      if (size !== undefined) Object.defineProperty(f, 'size', { value: size })
      return f
    }

    it('shows a duplicate notice when the hash was analysed before', async () => {
      stubDigest()
      const qs: (string | null)[] = []
      server.use(
        http.get('/api/tasks', ({ request }) => {
          const u = new URL(request.url)
          qs.push(u.searchParams.get('q'))
          expect(u.searchParams.get('take')).toBe('1')
          return HttpResponse.json({ items: [{ id: 'old-1', verdict: 'Malicious', createdAt: new Date().toISOString() }], total: 2 })
        }),
      )
      await renderPage()
      await userEvent.upload(screen.getByLabelText('File'), withBuffer('dup.exe'))

      expect(await screen.findByText(/Analysed 2 times before/)).toBeInTheDocument()
      expect(qs).toEqual(['ab'.repeat(32)])
      expect(screen.getByText('Malicious')).toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'View' })).toHaveAttribute('href', '/tasks/old-1')
    })

    it('skips the duplicate check for files over 512 MiB', async () => {
      stubDigest()
      let calls = 0
      server.use(
        http.get('/api/tasks', () => {
          calls++
          return HttpResponse.json({ items: [], total: 0 })
        }),
      )
      await renderPage()
      await userEvent.upload(screen.getByLabelText('File'), withBuffer('big.iso', 513 * 1024 ** 2))
      await screen.findByText('big.iso')
      await new Promise((r) => setTimeout(r, 50))
      expect(calls).toBe(0)
      expect(screen.queryByText(/Analysed/)).toBeNull()
    })

    it('hashes dropped files one at a time', async () => {
      let release: () => void = () => {}
      const digest = vi.fn()
        .mockImplementationOnce(() => new Promise<ArrayBuffer>((r) => { release = () => r(new Uint8Array(32).buffer) }))
        .mockResolvedValue(new Uint8Array(32).buffer)
      vi.stubGlobal('crypto', { subtle: { digest } })
      const first = withBuffer('one.exe')
      const second = withBuffer('two.exe')
      const read = vi.spyOn(second, 'arrayBuffer')
      await renderPage()
      await userEvent.upload(screen.getByLabelText('File'), [first, second])
      await waitFor(() => expect(digest).toHaveBeenCalledTimes(1))
      await new Promise((r) => setTimeout(r, 50))
      expect(read).not.toHaveBeenCalled() // the second file is not read while the first is hashing
      release()
      await waitFor(() => expect(digest).toHaveBeenCalledTimes(2))
      expect(read).toHaveBeenCalledOnce()
    })
  })

  describe('upload limit', () => {
    it('marks a file over the limit as a row error and uploads the rest of the batch', async () => {
      const names: string[] = []
      server.use(
        http.post('/api/tasks', async ({ request }) => {
          names.push(await ((await request.formData()).get('file') as File).text())
          return HttpResponse.json({ id: `t${names.length}` }, { status: 201 })
        }),
      )
      await renderPage('/submit', 1024)
      const big = new File(['x'], 'huge.iso')
      Object.defineProperty(big, 'size', { value: 4096 })
      await userEvent.upload(screen.getByLabelText('File'), [new File(['a'], 'a.bin'), big, new File(['b'], 'b.bin')])
      expect(await screen.findByText('huge.iso is larger than the 1.0 KiB limit.')).toBeInTheDocument()
      await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
      await submit()

      expect(await screen.findByText('Created 2 tasks')).toBeInTheDocument()
      expect(names).toEqual(['a', 'b'])
      expect(screen.getByText('huge.iso is larger than the 1.0 KiB limit.')).toBeInTheDocument()
    })

    it('does not upload a single file over the limit', async () => {
      countSubmits(() => HttpResponse.json({ id: 't1' }, { status: 201 }))
      await renderPage('/submit', 1024)
      const big = new File(['x'], 'huge.iso')
      Object.defineProperty(big, 'size', { value: 4096 })
      await userEvent.upload(screen.getByLabelText('File'), big)
      await userEvent.selectOptions(screen.getByLabelText(/organization/i), ORG.id)
      await submit()
      expect(await screen.findByText('Every chosen file is over the size limit. Choose a smaller file.')).toBeInTheDocument()
      expect(submitCalls).toBe(0)
    })
  })

  describe('resubmit', () => {
    const SOURCE = { id: 't1', orgId: ORG.id, originalFileName: 'invoice.exe', url: null, image: 'win11-26h2', networkMode: 'None', exitCountry: null, durationSec: 120, interactive: false }
    const source = (over: object = {}) => server.use(http.get('/api/tasks/t1', () => HttpResponse.json({ ...SOURCE, ...over })))

    it('resubmits from a task with prefilled options', async () => {
      source()
      let body: unknown
      server.use(
        http.post('/api/tasks/t1/resubmit', async ({ request }) => {
          body = await request.json()
          return HttpResponse.json({ id: 't2' }, { status: 201 })
        }),
      )
      await renderPage('/submit?from=t1')
      expect(await screen.findByText('invoice.exe')).toBeInTheDocument()
      expect(screen.queryByLabelText('File')).toBeNull()
      await waitFor(() => expect(screen.getByLabelText(/organization/i)).toHaveValue(ORG.id))
      expect(screen.getByLabelText(/network mode/i)).toHaveValue('None')
      expect(screen.getByLabelText(/duration/i)).toHaveValue(120)
      await submit()

      await waitFor(() => expect(screen.getByText('task page placeholder')).toBeInTheDocument())
      expect(body).toMatchObject({ orgId: ORG.id, networkMode: 'None', durationSec: 120, interactive: false })
    })

    it('ends resubmit mode with a toast when the sample expired', async () => {
      source()
      server.use(
        http.post('/api/tasks/t1/resubmit', () =>
          HttpResponse.json({ title: 'The sample has been deleted under the retention policy.', status: 409, type: 'sample-unavailable' }, { status: 409 }),
        ),
      )
      await renderPage('/submit?from=t1')
      await screen.findByText('invoice.exe')
      await waitFor(() => expect(screen.getByLabelText(/organization/i)).toHaveValue(ORG.id))
      await submit()

      expect(await screen.findByText('This sample has expired under retention. Upload it again.')).toBeInTheDocument()
      // the router applies the search-param change in a transition, so it can land after the toast
      await waitFor(() => expect(screen.queryByText('invoice.exe')).toBeNull())
      expect(await screen.findByLabelText('File')).toBeInTheDocument()
    })

    it('resubmits a URL task', async () => {
      source({ originalFileName: null, url: 'https://evil.test/payload' })
      await renderPage('/submit?from=t1')
      expect(await screen.findByText('https://evil.test/payload')).toBeInTheDocument()
    })

    for (const first of ['templates', 'source'] as const) {
      it(`falls back to the default template when the source template is not listed (${first} load first)`, async () => {
        const bodies: { image?: string }[] = []
        server.use(http.post('/api/tasks/t1/resubmit', async ({ request }) => {
          bodies.push((await request.json()) as { image?: string })
          return HttpResponse.json({ id: 't2' }, { status: 201 })
        }))
        let release: () => void = () => {}
        const gate = new Promise<void>((r) => { release = r })
        server.use(
          http.get('/api/templates', async () => {
            if (first === 'source') await gate
            return HttpResponse.json([WIN11, GOLDEN])
          }),
          http.get('/api/tasks/t1', async () => {
            if (first === 'templates') await gate
            return HttpResponse.json({ ...SOURCE, image: 'win10-hidden' })
          }),
        )
        await renderPage('/submit?from=t1')
        if (first === 'templates') await screen.findByLabelText('Template')
        else await screen.findByText('invoice.exe')
        release()
        await screen.findByText('invoice.exe')
        await waitFor(() => expect(screen.getByLabelText(/organization/i)).toHaveValue(ORG.id))
        expect(await screen.findByLabelText('Template')).toHaveValue(WIN11.id)
        await submit()
        await waitFor(() => expect(bodies).toHaveLength(1))
        expect(bodies[0].image).toBe(WIN11.id)
      })
    }

    it('sends no image when the picker is hidden and shows a server image error on the form', async () => {
      const bodies: Record<string, unknown>[] = []
      server.use(http.post('/api/tasks/t1/resubmit', async ({ request }) => {
        bodies.push((await request.json()) as Record<string, unknown>)
        return HttpResponse.json({ title: 'One or more validation errors occurred.', status: 400, errors: { image: ['Unknown or disabled template.'] } }, { status: 400 })
      }))
      source({ image: 'win10-hidden' })
      await renderPage('/submit?from=t1')
      await screen.findByText('invoice.exe')
      await waitFor(() => expect(templatesAnswered).toBe(true))
      await waitFor(() => expect(screen.getByLabelText(/organization/i)).toHaveValue(ORG.id))
      expect(screen.queryByLabelText('Template')).toBeNull()
      await submit()
      expect(await screen.findByText('Unknown or disabled template.')).toBeInTheDocument()
      expect(bodies[0]).not.toHaveProperty('image')
    })

    it('leaves resubmit mode through Change sample', async () => {
      source()
      await renderPage('/submit?from=t1')
      await screen.findByText('invoice.exe')
      await userEvent.click(screen.getByRole('button', { name: 'Change sample' }))
      await waitFor(() => expect(screen.queryByText('invoice.exe')).toBeNull())
      expect(await screen.findByLabelText('File')).toBeInTheDocument()
    })
  })
})
