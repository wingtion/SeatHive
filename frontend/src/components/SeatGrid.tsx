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
}

export function SeatGrid({ seats, mine, busySeats, canHold, onHold }: SeatGridProps) {
  return (
    <div className="flex flex-col gap-7">
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
                    aria-disabled={!free || !canHold || busy}
                    aria-label={`Seat ${seatLabel(seat)}, ${viewNames[view]}`}
                    onClick={() => {
                      if (free && canHold && !busy) onHold(seat.seatId)
                    }}
                    className={
                      'flex aspect-square w-full items-center justify-center rounded-seat font-mono text-xs tabular-nums ' +
                      'transition-colors duration-150 ' +
                      seatStyles[view] +
                      (free && canHold ? ' cursor-pointer hover:bg-ink/10 active:translate-y-px' : ' cursor-default') +
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
