import type { SeatView } from '../board/boardState'
import { seatStyles } from '../board/seatStyles'

// The mark of a kind of seat, as small as a legend needs it.
export function SeatMark({ view }: { view: SeatView }) {
  return <span aria-hidden className={`size-3 shrink-0 rounded-seat ${seatStyles[view]}`} />
}
