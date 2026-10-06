import type { SeatView } from '../board/boardState'

// A theatre seat as seen from the stage, in a box of 32 by 30: the backrest with its round top, the shoulders of
// the armrests, and the line of the cushion. How each kind of seat fills and outlines it is in index.css (.seat).
const OUTLINE =
  'M1.5 13.5Q1.5 12 3 12H6V5.5Q6 1.5 10 1.5H22Q26 1.5 26 5.5V12H29Q30.5 12 30.5 13.5V27Q30.5 28.5 29 28.5H3Q1.5 28.5 1.5 27Z'
const DETAIL = 'M6 12V28.5M26 12V28.5M6 19.5H26'

// Where the seat's number stands, from the top of the seat: the middle of the backrest.
export const SEAT_NUMBER_TOP = `${((10.6 / 30) * 100).toFixed(2)}%`

// Draws the seat into whatever holds it; that element carries the class "seat" and the kind as data-view.
// A mark is the seat at the size of the legend.
export function SeatGlyph({ view, mark = false }: { view: SeatView; mark?: boolean }) {
  // A seat someone else holds is hatched. The pattern is named here and not in the stylesheet, where a reference
  // to it would be looked up in the stylesheet's own file.
  const hatch = view === 'heldByOther' ? { fill: `url(#${mark ? 'seat-hatch-mark' : 'seat-hatch'})` } : undefined

  return (
    <svg viewBox={mark ? '0 -1 32 32' : '0 0 32 30'} aria-hidden className="block size-full overflow-visible">
      <path d={OUTLINE} className="seat-outline" style={hatch} />
      <path d={DETAIL} className="seat-detail" />
    </svg>
  )
}

// The hatch of a seat someone else holds: amber lines on the page colour, so it reads as "held" without looking
// like your own. Once on the page; the map's seats and the legend's marks refer to it.
export function SeatDefs() {
  return (
    <svg width="0" height="0" aria-hidden className="absolute">
      <defs>
        <Hatch id="seat-hatch" step={3.9} line={1.26} />
        <Hatch id="seat-hatch-mark" step={6.9} line={2.4} />
      </defs>
    </svg>
  )
}

function Hatch({ id, step, line }: { id: string; step: number; line: number }) {
  return (
    <pattern id={id} width={step} height={step} patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
      <rect width={step} height={step} style={{ fill: 'var(--bg)' }} />
      <line x1={step / 2} y1="0" x2={step / 2} y2={step} strokeWidth={line} style={{ stroke: 'var(--seat-amber-line)' }} />
    </pattern>
  )
}
