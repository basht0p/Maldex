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
| `tokens/maldex-tokens.css` | Color (including chart), font and radius tokens as CSS variables (dark default, light via `prefers-color-scheme` or `data-theme="light"`) |
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

## Chart colors

Charts never use the status colors or the accent. They have their own tokens.

| Token | Dark | Light | Use |
| --- | --- | --- | --- |
| `--mx-chart-1` | `#C440E0` | `#DC59F9` | Series 1 (orchid) |
| `--mx-chart-2` | `#009490` | `#03A4A1` | Series 2 (teal) |
| `--mx-chart-3` | `#7B56A2` | `#744F9A` | Series 3 (dusk violet) |
| `--mx-chart-4` | `#928401` | `#A49500` | Series 4 (olive) |
| `--mx-chart-highlight` | `#8F54F9` | `#6238EE` | The one series that matters |
| `--mx-chart-context` | `#5A6585` | `#7C869E` | Every other series beside the highlight |

- Assign series 1–4 in order and keep a series' color when filters hide others. Never cycle the colors. More than four series become an "Other" bucket, small multiples or a table.
- When one series is the point (this run against the rest), use the highlight and context pair instead of series colors.
- Every pair of series colors stays apart under protanopia and deuteranopia (OKLab ΔE ≥ 10) and meets 3:1 against the surface in both themes. Still label series with a legend, and with direct labels when there are four or fewer.

## Mark rules

- Clear space is two cells (about 31% of the mark width). Minimum size is 24 px, below that use the compact mark (down to 14 px).
- Do not recolor the cursor cell, stretch, rotate, outline or add effects to the mark. The only allowed animation is the cursor blinking in loading states.

## Not yet defined

- Outlined wordmark artwork for print.
