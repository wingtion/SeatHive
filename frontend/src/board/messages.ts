import { ApiError, NETWORK_ERROR } from '../api/client'

// What to tell the person when holding, confirming or releasing was refused, by the API's error code.
// Being turned away from a seat is an answer of the system, not a fault, and is worded that way.
const refusals: Record<string, string> = {
  seat_locked: 'Someone else was holding this seat at that very moment. The lock turned you away.',
  seat_held: 'Someone else holds this seat.',
  seat_already_booked: 'This seat is booked.',
  seat_not_found: 'This seat no longer exists. The demo data may have been reset.',
  hold_limit_reached: 'You hold as many seats as one person may. Confirm or release one first.',
  hold_expired: 'This hold ran out before it was confirmed.',
  hold_not_active: 'This hold is no longer active.',
  payment_in_progress: 'The payment for this seat is already in progress.',
  booking_not_found: 'This booking no longer exists. The demo data may have been reset.',
  not_hold_owner: 'This booking belongs to someone else.',
  rate_limited: 'Too many requests in a short time. Wait a minute, then try again.',
  [NETWORK_ERROR]: 'The server could not be reached. Check your connection, then try again.',
}

export function describeBookingFailure(error: unknown): string {
  if (error instanceof ApiError && refusals[error.code]) return refusals[error.code]
  return 'Something went wrong on the server. Try again.'
}

// The token was refused: it ran out, or its user is gone.
export function isSignedOut(error: unknown): boolean {
  return error instanceof ApiError && error.status === 401
}
