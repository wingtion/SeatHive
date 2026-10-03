import { useCallback, useEffect, useState } from 'react'
import { ApiError, NETWORK_ERROR } from '../api/client'
import { getEvents, getSeats, type EventSummary, type Seat, type SeatStatus } from '../api/events'
import { Button } from './Button'

type State =
  | { kind: 'loading' }
  | { kind: 'failed'; message: string }
  | { kind: 'empty' }
  | { kind: 'ready'; event: EventSummary; seats: Seat[] }

const eventDate = new Intl.DateTimeFormat('en-GB', { dateStyle: 'full', timeStyle: 'short' })

const statusLabels: Record<SeatStatus, string> = {
  available: 'Available',
  held: 'Held',
  booked: 'Booked',
}

// The event and how its seats stand right now, read from the API without a token.
export function EventOverview() {
  const [state, setState] = useState<State>({ kind: 'loading' })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const abort = new AbortController()

    async function load() {
      try {
        const events = await getEvents(abort.signal)
        const event = events.items[0]
        if (!event) {
          setState({ kind: 'empty' })
          return
        }

        const seats = await getSeats(event.id, abort.signal)
        setState({ kind: 'ready', event, seats: seats.items })
      } catch (error) {
        if (abort.signal.aborted) return
        setState({ kind: 'failed', message: describeFailure(error) })
      }
    }

    void load()
    return () => abort.abort()
  }, [attempt])

  const retry = useCallback(() => {
    setState({ kind: 'loading' })
    setAttempt((current) => current + 1)
  }, [])

  if (state.kind === 'loading') return <Loading />

  if (state.kind === 'failed') {
    return (
      <section className="flex flex-col items-start gap-4">
        <h1 className="text-2xl font-semibold tracking-tight">The event could not be loaded</h1>
        <p role="alert" className="max-w-[65ch] text-ink-muted">
          {state.message}
        </p>
        <Button onClick={retry}>Try again</Button>
      </section>
    )
  }

  if (state.kind === 'empty') {
    return (
      <section className="flex flex-col items-start gap-4">
        <h1 className="text-2xl font-semibold tracking-tight">No event yet</h1>
        <p className="max-w-[65ch] text-ink-muted">
          The demo data has not been created. It appears after an admin resets it, or after the nightly reset.
        </p>
        <Button onClick={retry}>Check again</Button>
      </section>
    )
  }

  const { event, seats } = state
  const counts = countByStatus(seats)

  return (
    <section className="flex flex-col gap-8">
      <div className="flex flex-col gap-1.5">
        <h1 className="text-2xl font-semibold tracking-tight sm:text-3xl">{event.name}</h1>
        <p className="text-ink-muted">
          <time dateTime={event.date}>{eventDate.format(new Date(event.date))}</time>
        </p>
      </div>

      <dl className="grid max-w-xl grid-cols-3 divide-x divide-line border-y border-line">
        {(Object.keys(statusLabels) as SeatStatus[]).map((status) => (
          <div key={status} className="flex flex-col gap-1 px-4 py-3 first:pl-0">
            <dt className="flex items-center gap-2 text-sm text-ink-muted">
              <Swatch status={status} />
              {statusLabels[status]}
            </dt>
            <dd className="font-mono text-2xl font-medium tabular-nums">{counts[status]}</dd>
          </div>
        ))}
      </dl>
    </section>
  )
}

// The same marks the seats will carry: told apart by shape and fill, not by colour alone.
function Swatch({ status }: { status: SeatStatus }) {
  const fill = {
    available: 'border border-line-strong',
    held: 'border border-hold-text bg-hold',
    booked: 'bg-ink',
  }[status]

  return <span aria-hidden className={`size-3 rounded-seat ${fill}`} />
}

function Loading() {
  return (
    <section aria-busy="true" className="flex flex-col gap-8">
      <p className="sr-only">Loading the event</p>
      <div className="flex flex-col gap-3">
        <div className="h-8 w-72 max-w-full animate-pulse rounded-control bg-ink/10" />
        <div className="h-5 w-56 max-w-full animate-pulse rounded-control bg-ink/10" />
      </div>
      <div className="h-[4.75rem] max-w-xl animate-pulse rounded-control bg-ink/10" />
    </section>
  )
}

function countByStatus(seats: Seat[]): Record<SeatStatus, number> {
  const counts: Record<SeatStatus, number> = { available: 0, held: 0, booked: 0 }
  for (const seat of seats) counts[seat.status] += 1
  return counts
}

function describeFailure(error: unknown): string {
  if (error instanceof ApiError && error.code === NETWORK_ERROR) {
    return 'The server could not be reached. Check your connection, then try again.'
  }
  if (error instanceof ApiError && error.code === 'rate_limited') {
    return 'Too many requests in a short time. Wait a minute, then try again.'
  }
  return 'Something went wrong on the server. Try again.'
}
