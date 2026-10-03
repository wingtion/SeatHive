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

export interface Section {
  name: string
  seats: Seat[]
}

// Sections in alphabetical order, each with its seats in row and seat order.
export function groupBySection(seats: Seat[]): Section[] {
  const sections = new Map<string, Seat[]>()
  for (const seat of seats) {
    const list = sections.get(seat.section) ?? []
    list.push(seat)
    sections.set(seat.section, list)
  }

  return [...sections.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([name, list]) => ({
      name,
      seats: list.sort((a, b) => a.row.localeCompare(b.row, undefined, { numeric: true }) || a.seatNumber - b.seatNumber),
    }))
}

export function seatLabel(seat: { section: string; seatNumber: number }): string {
  return `${seat.section} ${seat.seatNumber}`
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
