import { ApiError, NETWORK_ERROR } from '../api/client'

// Why a race did not start, by the API's error code. None of these is a fault of the system.
const raceRefusals: Record<string, string> = {
  race_in_progress: 'Another race is running right now. One runs at a time; try again in a moment.',
  racers_busy: 'Too few racers are free: winners of earlier races still hold their seats. Wait ten seconds, or use fewer racers.',
  seat_not_found: 'There is no free seat left to race for.',
  seat_held_by_race: 'The winner of an earlier race still holds this seat. It lets go in a few seconds.',
  seat_held: 'Someone holds this seat.',
  seat_already_booked: 'This seat is booked.',
  validation_failed: 'A race needs between 2 and 50 racers.',
  rate_limited: 'Five races a minute is the limit. Wait a minute, then try again.',
  [NETWORK_ERROR]: 'The server could not be reached. Check your connection, then try again.',
}

export function describeRaceFailure(error: unknown): string {
  if (error instanceof ApiError && raceRefusals[error.code]) return raceRefusals[error.code]
  return 'Something went wrong on the server. Try again.'
}

export function describeResetFailure(error: unknown): string {
  if (error instanceof ApiError && error.code === 'forbidden') return 'Only an admin may reset the demo data.'
  if (error instanceof ApiError && error.code === 'rate_limited') return 'Too many requests in a short time. Wait a minute, then try again.'
  if (error instanceof ApiError && error.code === NETWORK_ERROR) {
    return 'The server could not be reached. Check your connection, then try again.'
  }
  return 'The demo data could not be reset. Try again.'
}
