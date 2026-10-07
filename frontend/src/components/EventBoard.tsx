import { ArrowDown, X } from '@phosphor-icons/react'
import { useMemo, useState } from 'react'
import type { Booking } from '../api/bookings'
import type { Seat } from '../api/events'
import { useAuth } from '../auth/context'
import { activeBookings, bookingsBySeat, countSeats, seatLabel, type SeatView } from '../board/boardState'
import { useEventBoard } from '../board/useEventBoard'
import { useNow } from '../board/useNow'
import type { LiveStatus } from '../live/seatHub'
import { Button } from './Button'
import { Hood } from './Hood'
import { MySeats } from './MySeats'
import { SeatDefs } from './SeatGlyph'
import { SeatGrid } from './SeatGrid'
import { SeatMark } from './SeatMark'

const eventDate = new Intl.DateTimeFormat('en-GB', { dateStyle: 'full', timeStyle: 'short' })

// The stage: the event, its seat map and the person's own seats, all of it what the API holds right now.
export function EventBoard() {
  const { session, continueAsGuest } = useAuth()
  const board = useEventBoard()
  const mine = useMemo(() => bookingsBySeat(board.bookings), [board.bookings])
  const active = useMemo(() => activeBookings(board.bookings), [board.bookings])
  const now = useNow()
  // Someone not signed in chose a seat: the map answers with the one thing that is needed.
  const [askedToEnter, setAskedToEnter] = useState(false)
  const [entering, setEntering] = useState(false)
  const [enterError, setEnterError] = useState<string | null>(null)

  async function enter() {
    setEntering(true)
    setEnterError(null)
    try {
      await continueAsGuest()
      setAskedToEnter(false)
    } catch {
      setEnterError('Could not enter as a guest. Try again.')
    } finally {
      setEntering(false)
    }
  }

  if (board.load.kind === 'loading') return <Loading />

  if (board.load.kind === 'failed') {
    return (
      <section className="flex flex-col items-start gap-4">
        <h1 className="text-2xl font-semibold tracking-tight">The event could not be loaded</h1>
        <p role="alert" className="max-w-[65ch] text-ink-muted">
          {board.load.message}
        </p>
        <Button onClick={board.retry}>Try again</Button>
      </section>
    )
  }

  if (board.load.kind === 'empty') {
    return (
      <section className="flex flex-col items-start gap-4">
        <h1 className="text-2xl font-semibold tracking-tight">No event yet</h1>
        <p className="max-w-[65ch] text-ink-muted">
          The demo data has not been created. It appears after an admin resets it, or after the nightly reset.
        </p>
        <Button onClick={board.retry}>Check again</Button>
      </section>
    )
  }

  const { event } = board.load
  const seatWithError = board.seatError && board.seats.find((seat) => seat.seatId === board.seatError?.seatId)

  return (
    <div className="flex flex-col gap-8">
      <SeatDefs />
      {board.notice && (
        <div role="status" className="flex items-start justify-between gap-4 border-l-2 border-ink bg-surface py-3 pr-3 pl-4">
          <p className="text-sm">{board.notice}</p>
          <button
            type="button"
            onClick={board.dismissNotice}
            aria-label="Dismiss"
            className="-m-1 rounded-control p-1 text-ink-muted hover:bg-ink/5 hover:text-ink"
          >
            <X size={16} weight="bold" aria-hidden />
          </button>
        </div>
      )}

      <header className="flex flex-wrap items-end justify-between gap-x-8 gap-y-3">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-2xl font-semibold tracking-tight sm:text-3xl">{event.name}</h1>
          <p className="text-ink-muted">
            <time dateTime={event.date}>{eventDate.format(new Date(event.date))}</time>
          </p>
        </div>
        <Live status={board.live} signedIn={session !== null} />
      </header>

      {/* On a narrow screen the evidence is far below the map; this is the way to it. */}
      <a
        href="#hood-heading"
        className="flex w-fit items-center gap-2 rounded-control text-sm font-medium underline decoration-line-strong underline-offset-4 hover:decoration-ink lg:hidden"
      >
        <ArrowDown size={16} weight="bold" aria-hidden />
        Under the hood: race, holds, events
      </a>

      <div className="grid gap-10 lg:grid-cols-[minmax(0,27rem)_minmax(0,1fr)] lg:gap-16">
        {/* The stage: the booking itself. */}
        <div className="flex flex-col gap-10">
          <section aria-labelledby="seats-heading" className="flex flex-col gap-6">
            <h2 id="seats-heading" className="sr-only">
              Seats
            </h2>
            <Legend seats={board.seats} mine={mine} />

            {session === null && askedToEnter && (
              <div role="status" className="flex flex-wrap items-center gap-x-4 gap-y-2 border-l-2 border-ink pl-3 text-sm">
                <p>{enterError ?? 'Enter as a guest to hold a seat. It takes one click and no account.'}</p>
                <Button variant="primary" disabled={entering} onClick={() => void enter()}>
                  {entering ? 'Entering' : 'Continue as guest'}
                </Button>
              </div>
            )}

            {board.seatError && (
              <p role="alert" className="border-l-2 border-ink pl-3 text-sm">
                {seatWithError && <span className="font-mono font-medium">{seatLabel(seatWithError)}: </span>}
                {board.seatError.message}
              </p>
            )}

            <SeatGrid
              seats={board.seats}
              mine={mine}
              busySeats={board.busySeats}
              canHold={session !== null}
              onNeedsSignIn={() => setAskedToEnter(true)}
              onHold={(seatId) => void board.hold(seatId)}
            />
          </section>

          <MySeats
            signedIn={session !== null}
            bookings={active}
            now={now}
            busyBookings={board.busyBookings}
            errors={board.bookingErrors}
            lastEvents={board.lastEvents}
            onConfirm={(bookingId, fail) => void board.confirm(bookingId, fail)}
            onRelease={(bookingId) => void board.release(bookingId)}
          />
        </div>

        <aside className="lg:border-l lg:border-line lg:pl-16">
          <Hood board={board} session={session} mine={mine} now={now} />
        </aside>
      </div>
    </div>
  )
}

// How the seats stand, with the mark each kind carries on the map. The person's own seats are counted apart,
// so "Held" and "Booked" are the seats of others, as their marks say, and the four numbers add up.
function Legend({ seats, mine }: { seats: Seat[]; mine: Map<number, Booking> }) {
  const counts = countSeats(seats, mine)
  // "Yours" carries both marks a seat of your own can have on the map: held, and booked.
  const entries: { views: SeatView[]; label: string; value: number }[] = [
    { views: ['available'], label: 'Available', value: counts.available },
    { views: ['heldByOther'], label: 'Held', value: counts.heldByOthers },
    { views: ['booked'], label: 'Booked', value: counts.bookedByOthers },
    { views: ['mineHeld', 'mineBooked'], label: 'Yours', value: counts.yours },
  ]

  return (
    <dl className="grid max-w-[27rem] grid-cols-4 divide-x divide-line border-y border-line">
      {entries.map((entry) => (
        <div key={entry.label} className="flex flex-col gap-1 px-3 py-3 first:pl-0">
          <dt className="flex items-center gap-2 text-sm text-ink-muted">
            <span className="flex gap-0.5">
              {entry.views.map((view) => (
                <SeatMark key={view} view={view} />
              ))}
            </span>
            {entry.label}
          </dt>
          <dd className="font-mono text-xl font-medium tabular-nums">{entry.value}</dd>
        </div>
      ))}
    </dl>
  )
}

const liveText: Record<LiveStatus, string> = {
  live: 'Live',
  connecting: 'Connecting',
  reconnecting: 'Reconnecting',
  off: 'Not live',
}

// Whether the map changes by itself: the state of the connection to the API's hub, which needs a sign-in.
function Live({ status, signedIn }: { status: LiveStatus; signedIn: boolean }) {
  return (
    <p className="flex items-center gap-2 text-sm text-ink-muted" role="status">
      {/* Green, and only here: it says the connection is up. Not live is an empty ring, so colour is not the only cue. */}
      <span aria-hidden className={`size-2 rounded-full ${status === 'live' ? 'bg-live' : 'border border-line-strong'}`} />
      <span className={status === 'live' ? 'font-medium text-live' : ''}>{liveText[status]}</span>
      {!signedIn && <span>(a snapshot; enter as a guest for live updates)</span>}
    </p>
  )
}

function Loading() {
  return (
    <section aria-busy="true" className="flex flex-col gap-8">
      <p className="sr-only">Loading the event</p>
      <div className="flex flex-col gap-3">
        <div className="h-8 w-72 max-w-full animate-pulse rounded-control bg-ink/10" />
        <div className="h-5 w-56 max-w-full animate-pulse rounded-control bg-ink/10" />
      </div>
      <div className="h-[4.5rem] max-w-[27rem] animate-pulse rounded-control bg-ink/10" />
      <div className="h-64 max-w-[27rem] animate-pulse rounded-control bg-ink/10" />
    </section>
  )
}
