import { useState } from 'react'
import { deleteTemplate, discardTemplateVersion, publishTemplateVersion, rollBackTemplate, setDefaultTemplate, setTemplateEnabled } from '../../api/client'
import type { AdminTemplateDto, TemplateVersionDto } from '../../api/types'
import { useConfirm } from '../../kit/useConfirm'
import { useToast } from '../../kit/useToast'
import { messageOf } from '../../util/apiMessage'
import { safeText } from '../../util/safeText'
import { rollbackTarget } from './versions'

/**
 * The template mutations shared by the list and the detail page. Each runs while `busy`, announces its result with a toast,
 * then calls `onChanged` (a delete calls `onDeleted` instead when given). Errors, such as a 409 reason, are toasts.
 */
export function useTemplateActions(t: AdminTemplateDto, onChanged: () => Promise<void>, { onDeleted }: { onDeleted?: () => void } = {}) {
  const toast = useToast()
  const confirm = useConfirm()
  const [busy, setBusy] = useState(false)
  const name = safeText(t.displayName) // admin-entered: shown with control and bidi characters made visible

  async function run(action: () => Promise<unknown>, done: string, after: () => Promise<void> | void = onChanged): Promise<boolean> {
    setBusy(true)
    try {
      await action()
      toast.success(done)
      await after()
      return true
    } catch (e) {
      for (const m of messageOf(e)) toast.error(m)
      return false
    } finally {
      setBusy(false)
    }
  }

  async function publish(v: TemplateVersionDto) {
    const ok = await confirm({ title: `Publish v${v.number} of ${name}?`, body: `New tasks will use v${v.number}.`, confirmLabel: 'Publish' })
    if (ok) await run(() => publishTemplateVersion(t.id, v.id), `v${v.number} published`)
  }

  async function makeDefault() {
    const ok = await confirm({ title: `Make ${name} the default?`, body: 'New tasks use it when no template is chosen.', confirmLabel: 'Set as default' })
    if (ok) await run(() => setDefaultTemplate(t.id), `${name} is the default`)
  }

  async function toggleEnabled() {
    const enable = !t.enabled
    const ok = await confirm(enable
      ? { title: `Enable ${name}?`, body: 'Analysts can pick it for new tasks again.', confirmLabel: 'Enable' }
      : { title: `Disable ${name}?`, body: 'Analysts can no longer pick it for new tasks. Its versions are kept.', confirmLabel: 'Disable' })
    if (ok) await run(() => setTemplateEnabled(t.id, enable), enable ? 'Template enabled' : 'Template disabled')
  }

  async function discard(v: TemplateVersionDto) {
    const ok = await confirm({
      title: `Discard v${v.number} of ${name}?`,
      body: 'It is never published and its disk is deleted.',
      confirmLabel: 'Discard',
      danger: true,
    })
    if (ok) await run(() => discardTemplateVersion(t.id, v.id), `v${v.number} discarded`)
  }

  async function remove() {
    const ok = await confirm({
      title: `Delete ${name}?`,
      body: 'It has no versions; the template entry is removed.',
      confirmLabel: 'Delete',
      danger: true,
    })
    if (ok) await run(() => deleteTemplate(t.id), `${name} deleted`, onDeleted ?? onChanged)
  }

  async function rollBack() {
    const previous = rollbackTarget(t)
    if (!previous) return
    const ok = await confirm({
      title: `Roll back ${name} to v${previous.number}?`,
      body: `New tasks will use v${previous.number}.${t.current ? ` v${t.current.number} will be retired: a rolled-back version is never kept.` : ''}`,
      confirmLabel: 'Roll back',
      danger: true,
    })
    if (ok) await run(() => rollBackTemplate(t.id), `Rolled back to v${previous.number}`)
  }

  return { busy, publish, makeDefault, toggleEnabled, discard, remove, rollBack }
}
