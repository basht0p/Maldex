# MALdex brand assets

Identity direction: **Magic Bytes**. The mark is an M drawn in a 5×5 grid of byte cells. Unused cells are dim outlines (null bytes) and the center cell is the cursor, the only accent-colored part of the mark. The tagline tag `4D 5A` is the MZ magic number at the start of every Windows executable.

Dark mode is the primary theme.

## Contents

| Path | What it is |
| --- | --- |
| `logo/maldex-mark.svg` | Primary mark for dark backgrounds |
| `logo/maldex-mark-light.svg` | Primary mark for light backgrounds |
| `logo/maldex-mark-mono.svg` | One-color mark (no dim cells, cursor in the same ink) |
| `logo/maldex-mark-compact.svg` | Pixel-aligned mark for sizes under 24 px |
| `icons/app-icon.svg`, `icon-{180,192,512}.png` | App icon (rounded square), Apple touch and web manifest sizes |
| `icons/maskable-icon.svg`, `maskable-512.png` | Maskable icon with the mark inside the safe zone |
| `icons/favicon.svg`, `favicon-{16,32,48}.png` | Tab icon built from the compact mark |
| `tokens/maldex-tokens.css` | Color, font and radius tokens as CSS variables (dark default, light via `prefers-color-scheme` or `data-theme="light"`) |
| `tokens/tailwind.preset.cjs` | Tailwind theme extension that reads the CSS variables |

## Wordmark

`MALDEX` in capitals, Martian Mono Bold at width 112.5, with `DEX` in the accent color. In running text the product is written `MALdex`. The wordmark is live text, not an outlined SVG, so it needs the font loaded.

## Type

- **Martian Mono** (SIL OFL): hashes, rule names, IDs, paths, small caps labels and the wordmark.
- **Instrument Sans** (SIL OFL): headings, body, buttons and tables.

Both can be self-hosted so the console works on an isolated network.

## Color rules

- Accent blue marks things a user can act on: primary buttons, links, focus rings, selection and the cursor cell.
- Critical, warning, success and info describe analysis state only. Never show severity by color alone.
- Text pairs meet WCAG AA 4.5:1 and control borders meet 3:1 in both themes.

## Mark rules

- Clear space is two cells (about 31% of the mark width). Minimum size is 24 px, below that use the compact mark (down to 14 px).
- Do not recolor the cursor cell, stretch, rotate, outline or add effects to the mark. The only allowed animation is the cursor blinking in loading states.

## Not yet defined

- A categorical chart palette that stays distinct from the four status colors and works for color-blind viewers.
- Outlined wordmark artwork for print.
