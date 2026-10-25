/// <reference types="node" />
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const css = readFileSync('src/pages/TemplatesAdminPage.module.css', 'utf8') // vitest runs from ui/

const rule = (selector: string) => css.match(new RegExp(`${selector.replaceAll('.', '\\.')}\\s*\\{([^}]*)\\}`))?.[1] ?? ''

describe('start dialog layout (shared by New template, Edit and Refresh)', () => {
  it('lets the card contain its fields instead of growing with them', () => {
    expect(rule('.dialog')).toMatch(/box-sizing:\s*border-box/)
    expect(rule('.dialog')).toMatch(/grid-template-columns:\s*minmax\(0,\s*1fr\)/)
    expect(rule('.dialog input, .dialog select')).toMatch(/max-width:\s*100%/)
  })
})
