import { expect, it } from 'vitest'
import { safeReturnUrl } from './session'

it.each([
  [null, '/'],
  ['', '/'],
  ['/tasks?x=1', '/tasks?x=1'],
  ['//evil.example', '/'],
  ['/\\evil.example', '/'],
  ['https://evil.example', '/'],
  ['/\t/evil.example', '/'],
  ['/a\nb', '/'],
])('safeReturnUrl(%j) is %j', (raw, expected) => {
  expect(safeReturnUrl(raw)).toBe(expected)
})
