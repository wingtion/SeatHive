import type { SeatView } from '../board/boardState'
import { SeatGlyph } from './SeatGlyph'

// The mark of a kind of seat, as small as a legend needs it.
export function SeatMark({ view }: { view: SeatView }) {
  return (
    <span aria-hidden data-view={view} className="seat seat-mark size-3 shrink-0">
      <SeatGlyph view={view} mark />
    </span>
  )
}
