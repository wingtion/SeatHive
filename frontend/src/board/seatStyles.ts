import type { SeatView } from './boardState'

// How each kind of seat is drawn, on the map and in the legend. They differ in fill and border, not in colour
// alone: free is an outline, someone else's hold is hatched, your own is solid amber, booked is solid ink.
export const seatStyles: Record<SeatView, string> = {
  available: 'border border-line-strong text-ink',
  heldByOther: 'seat-hatch border border-hold-text text-ink',
  mineHeld: 'border border-hold-text bg-hold font-semibold text-hold-ink',
  minePaying: 'border border-hold-text bg-hold font-semibold text-hold-ink',
  booked: 'bg-ink text-bg',
  mineBooked: 'bg-ink text-bg shadow-[inset_0_0_0_2px_var(--hold)]',
}
