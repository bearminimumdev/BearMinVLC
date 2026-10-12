# BearMinVLC — logo mark options

Four directions that keep the client's core idea (bear + play), redrawn to fix the problems with the current three-circle mark.

All files use a 32×32 viewBox and contain only `<path>`/`<circle>` with `fill`/`stroke` attributes.
- `option-x.svg` is the two-colour version: amber `#F5A524`, with knockouts drawn in `#0B0B0C`.
- `option-x-mono.svg` is single-colour `currentColor`. Its holes are real cut-outs, made with opposite path winding rather than `fill-rule`, so the mark works on any background.

Straight edges and extremes sit on whole or half units. Curve control points are computed.

**What changed from the current mark** (applies to A, B and D, which share one head):
- The head is a 26×20 squircle instead of a circle. It is wide and flat-crowned like a bear's, not round like a mouse's.
- The ears are small (r 4.5) and sit about half-sunk into the crown. That proportion moves the read from Mickey to teddy.
- The play triangle's centroid sits on the head's centre line, not its bounding box, so it no longer looks shoved left.

I checked every option by rasterising it at 16, 18, 24, 32, 52 and 128–200 px on `#0B0B0C` and on light grey.

---

## A · Cub (`option-a.svg`)
A solid bear-head silhouette whose face is a play triangle cut out of it: one shape, one cut. It's the client's original idea, drawn properly. The squircle head, sunk ears and centred triangle make it read as a bear, not a mouse.

**At 16 px:** this is the strongest of the four. The silhouette stays solid, the ears are two clear 4 px bumps, and the play cut-out (about 6.5 px tall) stays readable. Two caveats:
- The 26-unit-wide head puts its sides on half-pixels (x = 1.5 / 14.5 px), so the outer columns are slightly soft. A hand-hinted 16 px ICO frame (sides nudged to whole pixels) fixes this.
- As with any small eared head, the ears can look a little pointed at 16 px.

## B · Den (`option-b.svg`)
The inverse: an amber app tile with the bear as a dark cut-out, and the play triangle sitting in its face. It's the "badge" direction, built for Start-menu and taskbar presence, and uses a scaled-down copy of A's head.

**At 16 px:** the tile is very legible and the dark bear (about 10 px) still reads. The play triangle shrinks to a 3–4 px wedge, so it shows as "something amber in the face". It's the heaviest option in an 18 px title bar.

*Why B isn't the brief's literal "(b) play triangle + two tiny ears":* I prototyped about a dozen variants. At icon sizes they read as a mitten, a chick, or a play button wearing Mickey ears, and a bare amber play triangle also drifts toward Infuse. A play triangle has no horizontal top for ears to sit on, so the bear never shows up. The B slot became the inverse/tile direction instead.

## C · Line (`option-c.svg`)
A single continuous 2-unit stroke draws the head and both ears in one closed line, around a solid play triangle. It's the most "bare minimum" option and sits naturally beside Windows 11 Fluent system icons.

**At 16 px:** the stroke is 1 px and its centre lines are on odd units, so the line is pixel-crisp at 16 and 32 px. The play stays solid and readable, and the ears become small rings. The trade-off is presence: next to solid app icons it looks light, and at 24 px the 1.5 px line is slightly soft.

## D · Snout (`option-d.svg`)
The universal round play button becomes the bear's muzzle: a dark circle in a solid head, with the amber triangle as the nose. It's my own pick for character. It reads as a bear first and a play button second, and makes a good mascot.

**At 16 px:** the bear survives, but the play button doesn't. The muzzle becomes a dark dot (it can look like an open "O" mouth) and the triangle collapses to about 3 px. From 24 px up it's the most charming of the four.

---

## Recommendation: A · Cub
It holds up best at 16–24 px, where the icon is actually seen (taskbar, title bar). It is also the most minimal: one silhouette and one cut, and the mono version is a single path. It keeps exactly the idea the client likes and fixes what was wrong with it. It reads as a bear, not Mickey, and the play triangle is optically centred, with no resemblance to VLC, YouTube, Plex or Infuse.

Because A, B and D share the same head, D can be added later as a large-format sibling (splash, About screen, empty states) without a second visual language.
