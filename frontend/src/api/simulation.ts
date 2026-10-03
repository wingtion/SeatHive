import { request } from './client'

// One racer's try. Times are milliseconds since the start of the race.
export interface RaceAttempt {
  racer: number
  outcome: 'won' | 'rejected' | 'error'
  // Why it lost: an error code of the hold, such as "seat_locked" or "seat_held".
  code: string | null
  // What happened at the Redis lock; "unavailable" when Redis could not be asked.
  lock: 'acquired' | 'busy' | 'unavailable' | null
  startedAtMs: number
  finishedAtMs: number
}

// What the starter gets back and what everyone watching the event is sent (raceFinished): the same thing.
export interface RaceReport {
  raceId: string
  eventId: number
  seatId: number
  racers: number
  winners: number
  startedAt: string
  durationMs: number
  // The winner keeps the seat until releasesAt, so the hold can be seen.
  winner: { racer: number; bookingId: number; releasesAt: string } | null
  attempts: RaceAttempt[]
}

// As the API limits them.
export const MIN_RACERS = 2
export const MAX_RACERS = 50
export const DEFAULT_RACERS = 20

// Racers try to hold one seat at the same moment: the free seat with the lowest id.
export function runRace(token: string, racers: number): Promise<RaceReport> {
  return request<RaceReport>('/api/simulation/simulate-concurrency', { method: 'POST', body: { racers }, token })
}
