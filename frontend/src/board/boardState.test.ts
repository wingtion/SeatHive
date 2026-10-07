import { describe, expect, it } from 'vitest'
import type { Booking, BookingStatus } from '../api/bookings'
import type { Seat, SeatStatus } from '../api/events'
import {
  activeBookings,
  applyBookingEvent,
  applySeatChange,
  bookingsBySeat,
  countSeats,
  formatRemaining,
  groupByRow,
  parseUtc,
  seatLabel,
  seatView,
} from './boardState'

function seat(seatId: number, status: SeatStatus = 'available', section = 'A', seatNumber = seatId): Seat {
  return { seatId, section, row: '1', seatNumber, status, heldUntil: null }
}

function booking(bookingId: number, seatId: number, status: BookingStatus): Booking {
  return {
    bookingId,
    status,
    createdAt: '2026-10-03T12:00:00Z',
    expiresAt: '2026-10-03T12:05:00Z',
    confirmedAt: null,
    seat: { seatId, section: 'A', row: '1', seatNumber: seatId },
    event: { id: 1, name: 'Event', date: '2026-11-02T17:00:00Z' },
  }
}

describe('applySeatChange', () => {
  it('changes the one seat and leaves the others as they are', () => {
    const seats = [seat(1), seat(2)]

    const changed = applySeatChange(seats, { seatId: 2, status: 'held', heldUntil: '2026-10-03T12:05:00Z' })

    expect(changed[0]).toBe(seats[0])
    expect(changed[1]).toMatchObject({ seatId: 2, status: 'held', heldUntil: '2026-10-03T12:05:00Z' })
  })

  it('ignores a seat it does not have', () => {
    const seats = [seat(1)]

    expect(applySeatChange(seats, { seatId: 9, status: 'booked', heldUntil: null })).toEqual(seats)
  })
})

describe('own bookings', () => {
  const bookings = [
    booking(5, 3, 'confirmed'),
    booking(4, 2, 'paymentPending'),
    booking(3, 1, 'held'),
    booking(2, 1, 'released'),
    booking(1, 1, 'expired'),
  ]

  it('counts only the ones that take a seat as active', () => {
    expect(activeBookings(bookings).map((b) => b.bookingId)).toEqual([5, 4, 3])
  })

  it('finds the active booking of a seat, not an earlier one that ended', () => {
    expect(bookingsBySeat(bookings).get(1)?.bookingId).toBe(3)
  })
})

describe('seatView', () => {
  it('shows what the API says for a seat that is not mine', () => {
    expect(seatView(seat(1, 'available'), undefined)).toBe('available')
    expect(seatView(seat(1, 'held'), undefined)).toBe('heldByOther')
    expect(seatView(seat(1, 'booked'), undefined)).toBe('booked')
  })

  it('shows my own booking by its status, whatever the seat says so far', () => {
    expect(seatView(seat(1, 'available'), booking(1, 1, 'held'))).toBe('mineHeld')
    expect(seatView(seat(1, 'held'), booking(1, 1, 'paymentPending'))).toBe('minePaying')
    expect(seatView(seat(1, 'booked'), booking(1, 1, 'confirmed'))).toBe('mineBooked')
  })
})

describe('applyBookingEvent', () => {
  const bookings = [booking(2, 2, 'paymentPending'), booking(1, 1, 'held')]
  const at = '2026-10-03T12:01:00Z'

  it.each([
    ['paymentRequested', 1, 'paymentPending'],
    ['paymentFailed', 2, 'held'],
    ['bookingConfirmed', 2, 'confirmed'],
    ['holdReleased', 1, 'released'],
    ['holdExpired', 1, 'expired'],
  ])('takes the status from a %s event', (type, bookingId, expected) => {
    const after = applyBookingEvent(bookings, { bookingId, type, occurredAt: at })

    expect(after?.find((b) => b.bookingId === bookingId)?.status).toBe(expected)
  })

  it('takes the time of the confirmation from the event', () => {
    const after = applyBookingEvent(bookings, { bookingId: 2, type: 'bookingConfirmed', occurredAt: at })

    expect(after?.find((b) => b.bookingId === 2)?.confirmedAt).toBe(at)
  })

  it.each(['paymentSucceeded', 'notificationSent', 'refundRequested', 'somethingNew'])(
    'leaves the status alone on a %s event',
    (type) => {
      expect(applyBookingEvent(bookings, { bookingId: 2, type, occurredAt: at })).toBe(bookings)
    },
  )

  it('answers null for a booking it does not have', () => {
    expect(applyBookingEvent(bookings, { bookingId: 9, type: 'seatHeld', occurredAt: at })).toBeNull()
  })
})

describe('groupByRow', () => {
  function hallSeat(seatId: number, row: string, seatNumber: number, section: string): Seat {
    return { seatId, section, row, seatNumber, status: 'available', heldUntil: null }
  }

  it('orders rows from the stage back and seats by number, whatever order they arrive in', () => {
    const rows = groupByRow([
      hallSeat(11, 'B', 2, 'Left'),
      hallSeat(3, 'A', 5, 'Centre'),
      hallSeat(1, 'A', 4, 'Centre'),
      hallSeat(2, 'A', 1, 'Left'),
      hallSeat(10, 'B', 1, 'Left'),
    ])

    expect(rows.map((row) => row.name)).toEqual(['A', 'B'])
    expect(rows[0].blocks.flat().map((s) => s.seatNumber)).toEqual([1, 4, 5])
  })

  it('starts a new block where the section changes', () => {
    const rows = groupByRow([
      hallSeat(1, 'A', 4, 'Centre'),
      hallSeat(2, 'A', 5, 'Centre'),
      hallSeat(3, 'A', 3, 'Left'),
      hallSeat(4, 'A', 8, 'Right'),
    ])

    expect(rows[0].blocks.map((block) => block.map((s) => s.seatNumber))).toEqual([[3], [4, 5], [8]])
  })
})

describe('seatLabel', () => {
  it('names a seat by its row and its number', () => {
    expect(seatLabel({ row: 'C', seatNumber: 7 })).toBe('C7')
  })
})

describe('time', () => {
  it('reads a timestamp without a zone as UTC', () => {
    expect(parseUtc('2026-10-03T12:00:00')).toBe(Date.UTC(2026, 9, 3, 12, 0, 0))
    expect(parseUtc('2026-10-03T12:00:00Z')).toBe(Date.UTC(2026, 9, 3, 12, 0, 0))
    expect(parseUtc('2026-10-03T15:00:00+03:00')).toBe(Date.UTC(2026, 9, 3, 12, 0, 0))
  })

  it.each([
    [300_000, '5:00'],
    [299_001, '5:00'],
    [59_000, '0:59'],
    [1, '0:01'],
    [0, '0:00'],
    [-5_000, '0:00'],
  ])('formats %i ms left as %s', (milliseconds, expected) => {
    expect(formatRemaining(milliseconds)).toBe(expected)
  })
})

describe('countSeats', () => {
  it('counts every seat once, the person\'s own apart from the others', () => {
    const seats = [seat(1), seat(2, 'held'), seat(3, 'held'), seat(4, 'booked'), seat(5, 'booked'), seat(6)]
    const mine = bookingsBySeat([booking(10, 3, 'held'), booking(11, 5, 'confirmed')])

    const counts = countSeats(seats, mine)

    expect(counts).toEqual({ available: 2, heldByOthers: 1, bookedByOthers: 1, yours: 2 })
    expect(counts.available + counts.heldByOthers + counts.bookedByOthers + counts.yours).toBe(seats.length)
  })

  it('counts a seat of the person\'s own as theirs before the map has heard of the hold', () => {
    const counts = countSeats([seat(1)], bookingsBySeat([booking(10, 1, 'held')]))

    expect(counts).toEqual({ available: 0, heldByOthers: 0, bookedByOthers: 0, yours: 1 })
  })
})
