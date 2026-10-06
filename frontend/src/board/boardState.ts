import type { Booking, BookingStatus } from '../api/bookings'
import type { Seat, SeatStatus } from '../api/events'

// What the hub says about a seat (seatStatusChanged), and what a hold answers.
export interface SeatChange {
  seatId: number
  status: SeatStatus
  heldUntil: string | null
}

// How a seat looks to the person at the screen. The API never says who holds a seat; "mine" is known
// only because the person's own bookings name their seats.
export type SeatView = 'available' | 'heldByOther' | 'booked' | 'mineHeld' | 'minePaying' | 'mineBooked'

export function applySeatChange(seats: Seat[], change: SeatChange): Seat[] {
  return seats.map((seat) =>
    seat.seatId === change.seatId ? { ...seat, status: change.status, heldUntil: change.heldUntil } : seat,
  )
}

// The bookings that take a seat right now, newest first as the API lists them.
export function activeBookings(bookings: Booking[]): Booking[] {
  return bookings.filter((b) => b.status === 'held' || b.status === 'paymentPending' || b.status === 'confirmed')
}

export function bookingsBySeat(bookings: Booking[]): Map<number, Booking> {
  return new Map(activeBookings(bookings).map((booking) => [booking.seat.seatId, booking]))
}

export function seatView(seat: Seat, mine: Booking | undefined): SeatView {
  if (mine) {
    if (mine.status === 'confirmed') return 'mineBooked'
    return mine.status === 'paymentPending' ? 'minePaying' : 'mineHeld'
  }

  if (seat.status === 'available') return 'available'
  return seat.status === 'held' ? 'heldByOther' : 'booked'
}

// How many seats there are of each kind the legend shows. Every seat is counted once: a seat of the person's own
// is "yours" and not also "held" or "booked", so the four numbers add up to the seats of the event.
export interface SeatCounts {
  available: number
  heldByOthers: number
  bookedByOthers: number
  yours: number
}

export function countSeats(seats: Seat[], mine: Map<number, Booking>): SeatCounts {
  const counts: SeatCounts = { available: 0, heldByOthers: 0, bookedByOthers: 0, yours: 0 }
  for (const seat of seats) {
    if (mine.has(seat.seatId)) counts.yours += 1
    else if (seat.status === 'available') counts.available += 1
    else if (seat.status === 'held') counts.heldByOthers += 1
    else counts.bookedByOthers += 1
  }
  return counts
}

// What an event of a booking (the hub's bookingEvent) means for its status. The event is the fact: reading the
// booking again at that moment can still show the old status, because the API announces an event to the client
// and changes the booking from the same message, in no fixed order. Events that leave the status alone are not here.
const statusAfter: Record<string, BookingStatus> = {
  seatHeld: 'held',
  paymentRequested: 'paymentPending',
  paymentFailed: 'held',
  bookingConfirmed: 'confirmed',
  holdReleased: 'released',
  holdExpired: 'expired',
}

// The bookings after an event. Null when the event is about a booking that is not in the list: read them again.
export function applyBookingEvent(
  bookings: Booking[],
  event: { bookingId: number; type: string; occurredAt: string },
): Booking[] | null {
  if (!bookings.some((booking) => booking.bookingId === event.bookingId)) return null

  const status = statusAfter[event.type]
  if (!status) return bookings

  return bookings.map((booking) =>
    booking.bookingId === event.bookingId
      ? { ...booking, status, confirmedAt: status === 'confirmed' ? event.occurredAt : booking.confirmedAt }
      : booking,
  )
}

// One row of the hall as it is drawn: its blocks from left to right, with an aisle between two blocks.
export interface SeatRow {
  name: string
  blocks: Seat[][]
}

// The rows of the hall from the stage to the back, each with its seats in seat order. Seats of one section
// that follow each other are a block; where the section changes there is an aisle. Nothing is known about the
// hall but the section, row and number of each seat.
export function groupByRow(seats: Seat[]): SeatRow[] {
  const rows = new Map<string, Seat[]>()
  for (const seat of seats) {
    const list = rows.get(seat.row) ?? []
    list.push(seat)
    rows.set(seat.row, list)
  }

  return [...rows.entries()]
    .sort(([a], [b]) => a.localeCompare(b, undefined, { numeric: true }))
    .map(([name, list]) => {
      const blocks: Seat[][] = []
      for (const seat of list.sort((a, b) => a.seatNumber - b.seatNumber || a.section.localeCompare(b.section))) {
        const block = blocks[blocks.length - 1]
        if (block && block[0].section === seat.section) block.push(seat)
        else blocks.push([seat])
      }
      return { name, blocks }
    })
}

// A seat as it is called in a hall: its row and its number in the row, "C7".
export function seatLabel(seat: { row: string; seatNumber: number }): string {
  return `${seat.row}${seat.seatNumber}`
}

// The API's timestamps are UTC. One without a zone would be read as local time by Date, so it is marked.
export function parseUtc(value: string): number {
  return Date.parse(/[zZ]|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`)
}

// Time left as m:ss, never below 0:00.
export function formatRemaining(milliseconds: number): string {
  const seconds = Math.max(0, Math.ceil(milliseconds / 1000))
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}
