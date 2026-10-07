import type { Booking } from '../api/bookings'
import type { Seat } from '../api/events'
import type { RaceAttempt, RaceReport } from '../api/simulation'
import { parseUtc } from '../board/boardState'

// What decided a racer's try. The lock turns most away; one that asks after the winner let the lock go
// is refused by the database, which has the hold by then.
export type AttemptKind = 'won' | 'lock' | 'database' | 'failed'

export function attemptKind(attempt: RaceAttempt): AttemptKind {
  if (attempt.outcome === 'won') return 'won'
  if (attempt.outcome === 'error') return 'failed'
  return attempt.code === 'seat_locked' ? 'lock' : 'database'
}

export function countAttempts(attempts: RaceAttempt[]): Record<AttemptKind, number> {
  const counts: Record<AttemptKind, number> = { won: 0, lock: 0, database: 0, failed: 0 }
  for (const attempt of attempts) counts[attemptKind(attempt)] += 1
  return counts
}

// A seat that is held right now, and the moment it is let go.
export interface HoldNow {
  seat: Seat
  // Milliseconds since the epoch.
  until: number
  holder: 'you' | 'race' | 'other'
}

// How long after its release time a race winner's seat is still taken for the winner's: the release is
// announced about a second later. A hold seen after that is someone else's.
const RELEASE_GRACE_MS = 2000

// The held seats, the one let go first on top. The seat status says a race winner's hold lasts as long as any
// other; the race report knows it is released sooner, so its time is used for that seat.
export function holdsNow(seats: Seat[], mine: Map<number, Booking>, race: RaceReport | null, now: number): HoldNow[] {
  const winnerUntil = race?.winner ? parseUtc(race.winner.releasesAt) : null

  return seats
    .filter((seat) => seat.status === 'held' && seat.heldUntil !== null)
    .map((seat): HoldNow => {
      if (mine.has(seat.seatId)) return { seat, until: parseUtc(seat.heldUntil!), holder: 'you' }
      if (race && winnerUntil !== null && seat.seatId === race.seatId && now < winnerUntil + RELEASE_GRACE_MS) {
        return { seat, until: winnerUntil, holder: 'race' }
      }
      return { seat, until: parseUtc(seat.heldUntil!), holder: 'other' }
    })
    .sort((a, b) => a.until - b.until || a.seat.seatId - b.seat.seatId)
}

// One line of a booking's timeline: from its history, or from the hub when the history does not have it yet.
export interface TimelineEntry {
  eventId: string
  type: string
  occurredAt: string
  detail: string | null
  simulated: boolean
  // The order the API recorded it in; missing for an event only the hub has told of so far.
  sequence?: number
}

// The order of a booking's life at one and the same moment, as the API's history orders it.
const lifeCycleRank: Record<string, number> = {
  seatHeld: 0,
  paymentRequested: 1,
  paymentSucceeded: 2,
  paymentFailed: 2,
  bookingConfirmed: 3,
  refundRequested: 3,
  notificationSent: 4,
  refundCompleted: 4,
  refundFailed: 4,
}

// The history and what the hub said, as one list in the order it happened. The hub is about a second ahead of
// the history and may say a thing twice; an event is one line, whoever told of it.
export function mergeTimeline(history: TimelineEntry[], live: TimelineEntry[]): TimelineEntry[] {
  const entries = new Map<string, TimelineEntry>()
  for (const entry of live) entries.set(entry.eventId, entry)
  // The history's entry wins: it carries the sequence.
  for (const entry of history) entries.set(entry.eventId, entry)

  return [...entries.values()].sort(
    (a, b) =>
      parseUtc(a.occurredAt) - parseUtc(b.occurredAt) ||
      (lifeCycleRank[a.type] ?? 5) - (lifeCycleRank[b.type] ?? 5) ||
      (a.sequence ?? Number.MAX_SAFE_INTEGER) - (b.sequence ?? Number.MAX_SAFE_INTEGER),
  )
}

const eventNames: Record<string, string> = {
  seatHeld: 'Seat held',
  holdReleased: 'Hold released',
  holdExpired: 'Hold ran out',
  paymentRequested: 'Payment requested',
  paymentSucceeded: 'Payment succeeded',
  paymentFailed: 'Payment failed',
  bookingConfirmed: 'Booking confirmed',
  refundRequested: 'Refund requested',
  refundCompleted: 'Refund completed',
  refundFailed: 'Refund failed',
  notificationSent: 'Notification sent',
}

export function eventName(type: string): string {
  return eventNames[type] ?? type
}

// Time between two events as the eye wants it: "+32 ms", "+1.4 s", "+12 s", "+3 min".
export function formatGap(milliseconds: number): string {
  const ms = Math.max(0, Math.round(milliseconds))
  if (ms < 1000) return `+${ms} ms`
  if (ms < 10_000) return `+${(ms / 1000).toFixed(1)} s`
  if (ms < 60_000) return `+${Math.round(ms / 1000)} s`
  return `+${Math.round(ms / 60_000)} min`
}
