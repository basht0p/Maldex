/**
 * Guest- or submitter-controlled strings (file names, URLs, failure reasons) are always rendered as React text, never HTML.
 * This additionally makes invisible/bidi control characters visible (e.g. the right-to-left override in "invoice‮txt.exe"
 * becomes "invoice\u{202E}txt.exe" literally) so a name cannot visually masquerade as another one.
 */
export function safeText(s: string | null | undefined): string {
  if (!s) return ''
  // C0/C1 controls, bidi controls, zero-width and other format characters.
  // oxlint-disable-next-line no-control-regex -- matching control characters is the point
  return s.replace(/[\u0000-\u001F\u007F-\u009F؜​-‏‪-‮⁠-⁤⁦-⁩﻿]/g, (c) => {
    const hex = c.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0')
    return `\\u${hex}`
  })
}
