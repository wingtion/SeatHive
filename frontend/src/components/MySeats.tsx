import { useId, useState } from 'react'
import type { Booking } from '../api/bookings'
import { formatRemaining, parseUtc, seatLabel } from '../board/boardState'
import type { BookingEventMessage } from '../live/seatHub'
import { Button } from './Button'

interface MySeatsProps {
  signedIn: boolean
  bookings: Booking[]
  now: number
  busyBookings: ReadonlySet<number>
  errors: Readonly<Record<number, string>>
  lastEvents: Readonly<Record<number, BookingEventMessage>>
  onConfirm: (bookingId: number, simulatePaymentFailure: boolean) => void
  onRelease: (bookingId: number) => void
}

// The person's own seats: what each is waiting for, and what they can do about it.
export function MySeats({ signedIn, bookings, now, busyBookings, errors, lastEvents, onConfirm, onRelease }: MySeatsProps) {
  return (
    <section aria-labelledby="my-seats-heading" className="flex flex-col gap-4">
      <h2 id="my-seats-heading" className="text-lg font-semibold">
        Your seats
      </h2>

      {bookings.length === 0 ? (
        <p className="text-sm text-ink-muted">
          {signedIn
            ? 'Pick a free seat on the map. It is held for you for a few minutes, until you confirm or release it.'
            : 'Enter as a guest to hold a seat and to see the map change live.'}
        </p>
      ) : (
        <ul className="flex flex-col divide-y divide-line border-y border-line">
          {bookings.map((booking) => (
            <BookingRow
              key={booking.bookingId}
              booking={booking}
              now={now}
              busy={busyBookings.has(booking.bookingId)}
              error={errors[booking.bookingId]}
              lastEvent={lastEvents[booking.bookingId]}
              onConfirm={onConfirm}
              onRelease={onRelease}
            />
          ))}
        </ul>
      )}
    </section>
  )
}

interface BookingRowProps {
  booking: Booking
  now: number
  busy: boolean
  error: string | undefined
  lastEvent: BookingEventMessage | undefined
  onConfirm: (bookingId: number, simulatePaymentFailure: boolean) => void
  onRelease: (bookingId: number) => void
}

function BookingRow({ booking, now, busy, error, lastEvent, onConfirm, onRelease }: BookingRowProps) {
  const failId = useId()
  const [failPayment, setFailPayment] = useState(false)
  const held = booking.status === 'held'
  const remaining = booking.expiresAt ? parseUtc(booking.expiresAt) - now : 0

  return (
    <li className="flex flex-col gap-3 py-4">
      <div className="flex items-baseline justify-between gap-4">
        <p className="font-mono text-lg font-medium">{seatLabel(booking.seat)}</p>
        <Status booking={booking} remaining={remaining} />
      </div>

      {held && lastEvent?.type === 'paymentFailed' && (
        <p className="text-sm text-ink-muted">The payment failed (simulated). The seat is still held for you.</p>
      )}

      {held && (
        <>
          <div className="flex flex-wrap gap-2">
            <Button variant="primary" disabled={busy} onClick={() => onConfirm(booking.bookingId, failPayment)}>
              Confirm and pay
            </Button>
            <Button disabled={busy} onClick={() => onRelease(booking.bookingId)}>
              Release
            </Button>
          </div>
          <label htmlFor={failId} className="flex items-center gap-2 text-sm text-ink-muted">
            <input
              id={failId}
              type="checkbox"
              checked={failPayment}
              onChange={(event) => setFailPayment(event.target.checked)}
              className="size-4 accent-ink"
            />
            Make this payment fail (demo switch)
          </label>
        </>
      )}

      {error && (
        <p role="alert" className="border-l-2 border-ink pl-3 text-sm">
          {error}
        </p>
      )}
    </li>
  )
}

function Status({ booking, remaining }: { booking: Booking; remaining: number }) {
  if (booking.status === 'confirmed') return <p className="text-sm font-medium">Booked</p>
  if (booking.status === 'paymentPending') {
    return <p className="text-sm text-hold-text">Payment in progress (simulated)</p>
  }

  return (
    <p className="text-sm text-hold-text">
      Held for{' '}
      <span className="font-mono font-medium tabular-nums" aria-live="off">
        {formatRemaining(remaining)}
      </span>
    </p>
  )
}
