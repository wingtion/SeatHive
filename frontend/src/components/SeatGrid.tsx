import type { Booking } from '../api/bookings'
import type { Seat } from '../api/events'
import { groupBySection, seatLabel, seatView, type SeatView } from '../board/boardState'
import { seatStyles } from '../board/seatStyles'

const viewNames: Record<SeatView, string> = {
  available: 'available',
  heldByOther: 'held by someone else',
  booked: 'booked',
  mineHeld: 'held by you',
  minePaying: 'held by you, payment in progress',
  mineBooked: 'booked by you',
}

interface SeatGridProps {
  seats: Seat[]
  mine: Map<number, Booking>
  busySeats: ReadonlySet<number>
  // False when nobody is signed in: the map can be read but no seat can be held.
  canHold: boolean
  onHold: (seatId: number) => void
  // A free seat was chosen by someone who is not signed in.
  onNeedsSignIn: () => void
}

export function SeatGrid({ seats, mine, busySeats, canHold, onHold, onNeedsSignIn }: SeatGridProps) {
  return (
    <div className="flex flex-col gap-7">
      {/* A hundred seats are a hundred tab stops; a keyboard gets past them in one. Seen only when focused. */}
      <a
        href="#my-seats-heading"
        className="sr-only rounded-control focus:not-sr-only focus:w-fit focus:px-2 focus:py-1 focus:text-sm focus:underline"
      >
        Skip the seat map
      </a>
      {groupBySection(seats).map((section) => (
        <div key={section.name} className="flex flex-col gap-3">
          <h3 className="text-sm font-medium text-ink-muted">Section {section.name}</h3>
          <ul className="grid max-w-[27rem] grid-cols-10 gap-1.5">
            {section.seats.map((seat) => {
              const view = seatView(seat, mine.get(seat.seatId))
              const free = view === 'available'
              const busy = busySeats.has(seat.seatId)

              return (
                <li key={seat.seatId}>
                  <button
                    type="button"
                    // Not the disabled attribute: a seat that cannot be held still says what it is to a screen reader.
                    aria-disabled={!free || busy}
                    aria-label={`Seat ${seatLabel(seat)}, ${viewNames[view]}`}
                    onClick={() => {
                      if (!free || busy) return
                      // Without a sign-in a free seat still answers: it says what is needed to hold it.
                      if (canHold) onHold(seat.seatId)
                      else onNeedsSignIn()
                    }}
                    className={
                      'flex aspect-square w-full items-center justify-center rounded-seat font-mono text-xs tabular-nums ' +
                      'transition-colors duration-150 ' +
                      seatStyles[view] +
                      (free ? ' cursor-pointer hover:bg-ink/10 active:translate-y-px' : ' cursor-default') +
                      (busy ? ' opacity-60' : '')
                    }
                  >
                    {seat.seatNumber}
                  </button>
                </li>
              )
            })}
          </ul>
        </div>
      ))}
    </div>
  )
}
