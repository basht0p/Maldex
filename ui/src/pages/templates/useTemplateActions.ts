import { useState } from 'react'
import { deleteTemplate, discardTemplateVersion, publishTemplateVersion, rollBackTemplate } from '../../api/client'
import type { AdminTemplateDto, TemplateVersionDto } from '../../api/types'
import { useConfirm } from '../../kit/useConfirm'
import { useToast } from '../../kit/useToast'
import { messageOf } from '../../util/apiMessage'
import { rollbackTarget } from './versions'

/**
 * The template mutations shared by the list and the detail page. Each runs while `busy`, announces its result with a toast,
 * then calls `onChanged` (a delete calls `onDeleted` instead when given). Errors, such as a 409 reason, are toasts.
 */
export function useTemplateActions(t: AdminTemplateDto, onChanged: () => Promise<void>, { onDeleted }: { onDeleted?: () => void } = {}) {
  const toast = useToast()
  const confirm = useConfirm()
  const [busy, setBusy] = useState(false)

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

  function publish(v: TemplateVersionDto) {
    return run(() => publishTemplateVersion(t.id, v.id), `v${v.number} published`)
  }

  async function discard(v: TemplateVersionDto) {
    const ok = await confirm({
      title: `Discard v${v.number} of ${t.displayName}?`,
      body: 'It is never published and its disk is deleted.',
      confirmLabel: 'Discard',
      danger: true,
    })
    if (ok) await run(() => discardTemplateVersion(t.id, v.id), `v${v.number} discarded`)
  }

  async function remove() {
    const ok = await confirm({
      title: `Delete ${t.displayName}?`,
      body: 'It has no versions; the template entry is removed.',
      confirmLabel: 'Delete',
      danger: true,
    })
    if (ok) await run(() => deleteTemplate(t.id), `${t.displayName} deleted`, onDeleted ?? onChanged)
  }

  async function rollBack() {
    const previous = rollbackTarget(t)
    if (!previous) return
    const ok = await confirm({
      title: `Roll back ${t.displayName} to v${previous.number}?`,
      body: `New tasks will use v${previous.number}.${t.current ? ` v${t.current.number} will be retired: a rolled-back version is never kept.` : ''}`,
      confirmLabel: 'Roll back',
      danger: true,
    })
    if (ok) await run(() => rollBackTemplate(t.id), `Rolled back to v${previous.number}`)
  }

  return { busy, confirm, run, publish, discard, remove, rollBack }
}
