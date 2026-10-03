import { useState } from 'react'
import type { Booking } from '../api/bookings'
import { parseUtc, seatLabel } from '../board/boardState'
import { eventName, formatGap, mergeTimeline } from '../hood/hoodState'
import { useBookingHistory } from '../hood/useTimeline'
import type { BookingEventMessage } from '../live/seatHub'

const clock = new Intl.DateTimeFormat('en-GB', {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  fractionalSecondDigits: 3,
})

// The newest bookings the person can choose between.
const CHOICES = 4

interface TimelineProps {
  token: string | null
  // The person's own bookings, newest first.
  bookings: Booking[]
  liveEvents: readonly BookingEventMessage[]
  resyncs: number
}

// What happened to one of the person's bookings, event by event: the API's history, kept current by the hub.
export function Timeline({ token, bookings, liveEvents, resyncs }: TimelineProps) {
  const choices = bookings.slice(0, CHOICES)
  const newest = choices[0]?.bookingId ?? null
  // A choice holds until a newer booking appears: the timeline then follows what the person just did.
  const [picked, setPicked] = useState<{ bookingId: number; newest: number | null } | null>(null)
  const chosen =
    picked && picked.newest === newest && choices.some((booking) => booking.bookingId === picked.bookingId)
      ? picked.bookingId
      : newest

  const history = useBookingHistory(token, chosen, resyncs)
  const entries = mergeTimeline(
    history.entries,
    liveEvents.filter((event) => event.bookingId === chosen),
  )

  return (
    <section aria-labelledby="timeline-heading" className="flex flex-col gap-4">
      <div className="flex flex-col gap-1.5">
        <h3 id="timeline-heading" className="text-lg font-semibold">
          What happened to your booking
        </h3>
        <p className="max-w-[65ch] text-sm text-ink-muted">
          Each line is a message that went through the outbox and RabbitMQ. Payments and notifications are simulated by a
          separate worker.
        </p>
      </div>

      {token === null ? (
        <p className="text-sm text-ink-muted">Enter as a guest and hold a seat to see its events arrive.</p>
      ) : chosen === null ? (
        <p className="text-sm text-ink-muted">Hold a seat on the map. Its events appear here as they arrive.</p>
      ) : (
        <>
          {choices.length > 1 && (
            <div className="flex flex-wrap gap-2" role="group" aria-label="Booking">
              {choices.map((booking) => {
                const active = booking.bookingId === chosen
                return (
                  <button
                    key={booking.bookingId}
                    type="button"
                    aria-pressed={active}
                    onClick={() => setPicked({ bookingId: booking.bookingId, newest })}
                    className={
                      'h-8 rounded-control border px-2.5 font-mono text-sm transition-colors duration-150 active:translate-y-px ' +
                      (active ? 'border-ink bg-ink text-bg' : 'border-line-strong text-ink hover:bg-ink/5')
                    }
                  >
                    {seatLabel(booking.seat)}
                  </button>
                )
              })}
            </div>
          )}

          {history.status === 'failed' && (
            <p role="alert" className="border-l-2 border-ink pl-3 text-sm">
              The history could not be read. Events that arrive from now on are still shown.
            </p>
          )}

          {entries.length === 0 ? (
            history.status === 'loading' ? (
              <div aria-busy="true" className="flex max-w-md flex-col gap-2">
                <p className="sr-only">Loading the history</p>
                <div className="h-5 animate-pulse rounded-control bg-ink/10" />
                <div className="h-5 w-4/5 animate-pulse rounded-control bg-ink/10" />
              </div>
            ) : (
              history.status === 'ready' && <p className="text-sm text-ink-muted">Nothing recorded yet. The first event is about a second away.</p>
            )
          ) : (
            <ol className="flex flex-col gap-2.5" aria-live="polite">
              {entries.map((entry, index) => {
                const before = entries[index - 1]
                return (
                  <li key={entry.eventId} className="grid grid-cols-[6.5rem_1fr] items-baseline gap-x-3 text-sm sm:grid-cols-[6.5rem_1fr_auto]">
                    <time dateTime={entry.occurredAt} className="font-mono text-xs tabular-nums text-ink-muted">
                      {clock.format(parseUtc(entry.occurredAt))}
                    </time>
                    <p className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                      <span className="font-medium">{eventName(entry.type)}</span>
                      {entry.simulated && (
                        <span className="rounded-seat border border-line-strong px-1.5 py-px font-mono text-xs text-ink-muted">simulated</span>
                      )}
                      {/* The API's detail of a simulated step is the word itself; the tag says it already. */}
                      {entry.detail && entry.detail !== 'simulated' && <span className="text-ink-muted">{entry.detail}</span>}
                    </p>
                    <span className="col-start-2 font-mono text-xs tabular-nums text-ink-muted sm:col-start-3">
                      {before ? formatGap(parseUtc(entry.occurredAt) - parseUtc(before.occurredAt)) : ''}
                    </span>
                  </li>
                )
              })}
            </ol>
          )}
        </>
      )}
    </section>
  )
}
