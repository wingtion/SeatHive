import { describe, expect, it } from 'vitest'
import type { Booking } from '../api/bookings'
import type { Seat } from '../api/events'
import type { RaceAttempt, RaceReport } from '../api/simulation'
import { attemptKind, countAttempts, eventName, formatGap, holdsNow, mergeTimeline, type TimelineEntry } from './hoodState'

function attempt(racer: number, outcome: RaceAttempt['outcome'], code: string | null, lock: RaceAttempt['lock']): RaceAttempt {
  return { racer, outcome, code, lock, startedAtMs: 0, finishedAtMs: 5 }
}

function seat(seatId: number, heldUntil: string | null): Seat {
  return { seatId, section: 'A', row: '1', seatNumber: seatId, status: heldUntil ? 'held' : 'available', heldUntil }
}

function race(seatId: number, releasesAt: string | null): RaceReport {
  return {
    raceId: 'race-1',
    eventId: 1,
    seatId,
    racers: 2,
    winners: releasesAt ? 1 : 0,
    startedAt: '2026-10-03T12:00:00Z',
    durationMs: 12,
    winner: releasesAt ? { racer: 1, bookingId: 9, releasesAt } : null,
    attempts: [],
  }
}

function entry(eventId: string, type: string, occurredAt: string, sequence?: number): TimelineEntry {
  return { eventId, type, occurredAt, detail: null, simulated: false, sequence }
}

const at = (time: string) => Date.parse(`2026-10-03T${time}Z`)

describe('attemptKind', () => {
  it('tells the winner, the lock, the database and a failure apart', () => {
    expect(attemptKind(attempt(1, 'won', null, 'acquired'))).toBe('won')
    expect(attemptKind(attempt(2, 'rejected', 'seat_locked', 'busy'))).toBe('lock')
    expect(attemptKind(attempt(3, 'rejected', 'seat_held', 'acquired'))).toBe('database')
    expect(attemptKind(attempt(4, 'rejected', 'seat_held', 'unavailable'))).toBe('database')
    expect(attemptKind(attempt(5, 'error', 'internal_error', null))).toBe('failed')
  })
})

describe('countAttempts', () => {
  it('counts every attempt once', () => {
    const counts = countAttempts([
      attempt(1, 'won', null, 'acquired'),
      attempt(2, 'rejected', 'seat_locked', 'busy'),
      attempt(3, 'rejected', 'seat_locked', 'busy'),
      attempt(4, 'rejected', 'seat_held', 'acquired'),
    ])

    expect(counts).toEqual({ won: 1, lock: 2, database: 1, failed: 0 })
  })
})

describe('holdsNow', () => {
  it('lists held seats only, the one let go first on top', () => {
    const seats = [seat(1, '2026-10-03T12:05:00Z'), seat(2, null), seat(3, '2026-10-03T12:03:00Z')]

    const holds = holdsNow(seats, new Map(), null, at('12:00:00'))

    expect(holds.map((hold) => hold.seat.seatId)).toEqual([3, 1])
    expect(holds[0]).toMatchObject({ until: at('12:03:00'), holder: 'other' })
  })

  it("marks the person's own holds", () => {
    const mine = new Map<number, Booking>([[1, {} as Booking]])

    const holds = holdsNow([seat(1, '2026-10-03T12:05:00Z')], mine, null, at('12:00:00'))

    expect(holds[0].holder).toBe('you')
  })

  it('counts a race winner down to its release, not to the end of the hold', () => {
    const holds = holdsNow([seat(1, '2026-10-03T12:05:00Z')], new Map(), race(1, '2026-10-03T12:00:10Z'), at('12:00:04'))

    expect(holds[0]).toMatchObject({ until: at('12:00:10'), holder: 'race' })
  })

  it('takes a hold on that seat for someone else once the winner has let go', () => {
    const holds = holdsNow([seat(1, '2026-10-03T12:06:00Z')], new Map(), race(1, '2026-10-03T12:00:10Z'), at('12:00:30'))

    expect(holds[0]).toMatchObject({ until: at('12:06:00'), holder: 'other' })
  })

  it('leaves other seats alone when a race has a winner', () => {
    const holds = holdsNow([seat(2, '2026-10-03T12:05:00Z')], new Map(), race(1, '2026-10-03T12:00:10Z'), at('12:00:04'))

    expect(holds[0].holder).toBe('other')
  })
})

describe('mergeTimeline', () => {
  it('adds what only the hub has told of', () => {
    const history = [entry('a', 'seatHeld', '2026-10-03T12:00:00Z', 1)]
    const live = [entry('b', 'paymentRequested', '2026-10-03T12:00:05Z')]

    expect(mergeTimeline(history, live).map((e) => e.eventId)).toEqual(['a', 'b'])
  })

  it('keeps one line for an event both have, the one from the history', () => {
    const history = [entry('a', 'seatHeld', '2026-10-03T12:00:00Z', 1)]
    const live = [entry('a', 'seatHeld', '2026-10-03T12:00:00Z'), entry('a', 'seatHeld', '2026-10-03T12:00:00Z')]

    const merged = mergeTimeline(history, live)

    expect(merged).toHaveLength(1)
    expect(merged[0].sequence).toBe(1)
  })

  it('orders by time, whatever order the events arrived in', () => {
    const live = [
      entry('c', 'bookingConfirmed', '2026-10-03T12:00:07Z'),
      entry('a', 'seatHeld', '2026-10-03T12:00:00Z'),
      entry('b', 'paymentRequested', '2026-10-03T12:00:05Z'),
    ]

    expect(mergeTimeline([], live).map((e) => e.eventId)).toEqual(['a', 'b', 'c'])
  })

  it('orders events of the same moment by the life of a booking', () => {
    const moment = '2026-10-03T12:00:07Z'
    const live = [entry('n', 'notificationSent', moment), entry('c', 'bookingConfirmed', moment), entry('p', 'paymentSucceeded', moment)]

    expect(mergeTimeline([], live).map((e) => e.eventId)).toEqual(['p', 'c', 'n'])
  })

  it('reads a timestamp without a zone as UTC', () => {
    const merged = mergeTimeline([entry('b', 'holdReleased', '2026-10-03T12:00:05', 2)], [entry('a', 'seatHeld', '2026-10-03T12:00:00Z')])

    expect(merged.map((e) => e.eventId)).toEqual(['a', 'b'])
  })
})

describe('eventName', () => {
  it('names the events the API sends and passes on one it does not know', () => {
    expect(eventName('paymentRequested')).toBe('Payment requested')
    expect(eventName('somethingNew')).toBe('somethingNew')
  })
})

describe('formatGap', () => {
  it('shows milliseconds below a second, tenths below ten, then seconds, then minutes', () => {
    expect(formatGap(0)).toBe('+0 ms')
    expect(formatGap(32)).toBe('+32 ms')
    expect(formatGap(1432)).toBe('+1.4 s')
    expect(formatGap(12_400)).toBe('+12 s')
    expect(formatGap(185_000)).toBe('+3 min')
  })

  it('never goes below zero', () => {
    expect(formatGap(-40)).toBe('+0 ms')
  })
})
