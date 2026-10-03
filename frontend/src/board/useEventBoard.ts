import { useCallback, useEffect, useRef, useState } from 'react'
import { confirmBooking, getMyBookings, holdSeat, releaseBooking, type Booking } from '../api/bookings'
import { ApiError, NETWORK_ERROR } from '../api/client'
import { getEvents, getSeats, type EventSummary, type Seat } from '../api/events'
import { resetDemoData } from '../api/setup'
import { runRace, type RaceReport } from '../api/simulation'
import { useAuth } from '../auth/context'
import { watchEvent, type BookingEventMessage, type LiveStatus } from '../live/seatHub'
import { applyBookingEvent, applySeatChange } from './boardState'
import { describeRaceFailure, describeResetFailure } from '../hood/messages'
import { describeBookingFailure, isSignedOut } from './messages'

type Load =
  | { kind: 'loading' }
  | { kind: 'failed'; message: string }
  | { kind: 'empty' }
  | { kind: 'ready'; event: EventSummary }

export interface EventBoard {
  load: Load
  seats: Seat[]
  // The signed-in person's own bookings, newest first; empty when nobody is signed in.
  bookings: Booking[]
  live: LiveStatus
  // Something the person should know that is nobody's fault: the data was reset, the session ended.
  notice: string | null
  dismissNotice: () => void
  retry: () => void
  busySeats: ReadonlySet<number>
  busyBookings: ReadonlySet<number>
  seatError: { seatId: number; message: string } | null
  bookingErrors: Readonly<Record<number, string>>
  // The latest thing the API said happened to a booking, by booking id.
  lastEvents: Readonly<Record<number, BookingEventMessage>>
  // Everything the hub said about the person's bookings since the page was opened, each event once.
  liveEvents: readonly BookingEventMessage[]
  // Goes up when the connection came back after a gap: what is read once should be read again.
  resyncs: number
  // The latest race on this event, whoever started it.
  race: RaceReport | null
  raceRunning: boolean
  raceError: string | null
  startRace: (racers: number) => Promise<void>
  // Admin only. Answers what went wrong, or null when the data was reset.
  resetDemoData: () => Promise<string | null>
  hold: (seatId: number) => Promise<void>
  confirm: (bookingId: number, simulatePaymentFailure: boolean) => Promise<void>
  release: (bookingId: number) => Promise<void>
}

// The event, its seats and the person's bookings, kept current: read over HTTP, then changed by what the hub sends.
export function useEventBoard(): EventBoard {
  const { session, signOut } = useAuth()
  const token = session?.token ?? null

  const [load, setLoad] = useState<Load>({ kind: 'loading' })
  const [seats, setSeats] = useState<Seat[]>([])
  // Kept with the token they were read for, so one person's bookings are never shown to the next.
  const [own, setOwn] = useState<{ owner: string | null; items: Booking[] }>({ owner: null, items: [] })
  const [connection, setConnection] = useState<LiveStatus>('off')
  const [notice, setNotice] = useState<string | null>(null)
  const [reloads, setReloads] = useState(0)
  const [busySeats, setBusySeats] = useState<ReadonlySet<number>>(new Set())
  const [busyBookings, setBusyBookings] = useState<ReadonlySet<number>>(new Set())
  const [seatError, setSeatError] = useState<EventBoard['seatError']>(null)
  const [bookingErrors, setBookingErrors] = useState<Record<number, string>>({})
  const [lastEvents, setLastEvents] = useState<Record<number, BookingEventMessage>>({})
  const [liveEvents, setLiveEvents] = useState<readonly BookingEventMessage[]>([])
  const [resyncs, setResyncs] = useState(0)
  const [race, setRace] = useState<RaceReport | null>(null)
  const [raceRunning, setRaceRunning] = useState(false)
  const [raceError, setRaceError] = useState<string | null>(null)

  const eventId = load.kind === 'ready' ? load.event.id : null
  const bookings = token !== null && own.owner === token ? own.items : NO_BOOKINGS
  // There is a connection only for a signed-in person on an event that exists.
  const live = token !== null && eventId !== null ? connection : 'off'

  // Read by callbacks that outlive a render (hub handlers), so they never act for an earlier session or event.
  const current = useRef({ token, eventId, bookings })
  useEffect(() => {
    current.current = { token, eventId, bookings }
  }, [token, eventId, bookings])

  const endSession = useCallback(() => {
    signOut()
    setNotice('Your session has ended. Continue as a guest or sign in to go on.')
  }, [signOut])

  // The event and its seats. Readable without a token.
  useEffect(() => {
    const abort = new AbortController()

    async function read() {
      try {
        const event = (await getEvents(abort.signal)).items[0]
        if (!event) {
          setSeats([])
          setLoad({ kind: 'empty' })
          return
        }

        const page = await getSeats(event.id, abort.signal)
        setSeats(page.items)
        setLoad({ kind: 'ready', event })
      } catch (error) {
        if (abort.signal.aborted) return
        setLoad({ kind: 'failed', message: describeLoadFailure(error) })
      }
    }

    void read()
    return () => abort.abort()
  }, [reloads])

  // Everything on screen came from data that is gone: it is cleared and read again.
  const dataWasReset = useCallback(() => {
    setNotice('The demo data was reset: every booking is gone and all seats are free again.')
    setSeatError(null)
    setBookingErrors({})
    setLastEvents({})
    setLiveEvents([])
    setRace(null)
    setRaceError(null)
    setReloads((count) => count + 1)
  }, [])

  const refreshSeats = useCallback(async () => {
    const id = current.current.eventId
    if (id === null) return

    try {
      const page = await getSeats(id)
      if (current.current.eventId === id) setSeats(page.items)
    } catch {
      // The seats on screen stay; the hub or the next action brings them up to date.
    }
  }, [])

  const refreshBookings = useCallback(async () => {
    const asked = current.current.token
    if (!asked) return

    try {
      const page = await getMyBookings(asked)
      if (current.current.token === asked) setOwn({ owner: asked, items: page.items })
    } catch (error) {
      if (isSignedOut(error) && current.current.token === asked) endSession()
    }
  }, [endSession])

  // The person's own bookings: read when they sign in and after a reload.
  useEffect(() => {
    if (!token) return

    const abort = new AbortController()
    getMyBookings(token, abort.signal)
      .then((page) => setOwn({ owner: token, items: page.items }))
      .catch((error: unknown) => {
        if (abort.signal.aborted) return
        if (isSignedOut(error)) endSession()
      })
    return () => abort.abort()
  }, [token, reloads, endSession])

  // Live updates, for a signed-in person on an event that exists.
  useEffect(() => {
    if (!token || eventId === null) return

    return watchEvent(token, eventId, {
      onStatus: setConnection,
      onSeatChanged: (change) => setSeats((seatsNow) => applySeatChange(seatsNow, change)),
      onBookingEvent: (message) => {
        setLastEvents((events) => ({ ...events, [message.bookingId]: message }))
        // The hub may send an event twice; it is kept once.
        setLiveEvents((events) =>
          events.some((known) => known.eventId === message.eventId) ? events : [...events, message].slice(-MAX_LIVE_EVENTS),
        )

        // The event says what the booking is now; only a booking not seen before is read from the API.
        const known = applyBookingEvent(current.current.bookings, message)
        if (known === null) void refreshBookings()
        else setOwn((ownNow) => ({ owner: ownNow.owner, items: applyBookingEvent(ownNow.items, message) ?? ownNow.items }))
      },
      onRaceFinished: setRace,
      onDemoDataReset: dataWasReset,
      onResync: () => {
        setResyncs((count) => count + 1)
        void refreshSeats()
        void refreshBookings()
      },
    })
  }, [token, eventId, refreshBookings, refreshSeats, dataWasReset])

  const hold = useCallback(
    async (seatId: number) => {
      if (!token) return

      setSeatError(null)
      setBusySeats((busy) => new Set(busy).add(seatId))
      try {
        const held = await holdSeat(token, seatId)
        // Shown at once; the hub confirms it about a second later.
        setSeats((seatsNow) => applySeatChange(seatsNow, { seatId, status: 'held', heldUntil: held.expiresAt }))
        await refreshBookings()
      } catch (error) {
        if (isSignedOut(error)) {
          endSession()
        } else {
          setSeatError({ seatId, message: describeBookingFailure(error) })
          // Being turned away means the seat is not what the screen showed.
          void refreshSeats()
        }
      } finally {
        setBusySeats((busy) => without(busy, seatId))
      }
    },
    [token, refreshBookings, refreshSeats, endSession],
  )

  const act = useCallback(
    async (bookingId: number, action: (token: string) => Promise<void>) => {
      if (!token) return

      setBookingErrors((errors) => withoutKey(errors, bookingId))
      setBusyBookings((busy) => new Set(busy).add(bookingId))
      try {
        await action(token)
        await Promise.all([refreshBookings(), refreshSeats()])
      } catch (error) {
        if (isSignedOut(error)) {
          endSession()
        } else {
          setBookingErrors((errors) => ({ ...errors, [bookingId]: describeBookingFailure(error) }))
          void refreshBookings()
        }
      } finally {
        setBusyBookings((busy) => without(busy, bookingId))
      }
    },
    [token, refreshBookings, refreshSeats, endSession],
  )

  const confirm = useCallback(
    (bookingId: number, simulatePaymentFailure: boolean) =>
      act(bookingId, (asToken) => confirmBooking(asToken, bookingId, simulatePaymentFailure)),
    [act],
  )

  const release = useCallback(
    (bookingId: number) => act(bookingId, (asToken) => releaseBooking(asToken, bookingId)),
    [act],
  )

  const startRace = useCallback(
    async (racers: number) => {
      if (!token) return

      setRaceError(null)
      setRaceRunning(true)
      try {
        // The hub sends the same report to everyone watching; the starter has it from the answer already.
        setRace(await runRace(token, racers))
        void refreshSeats()
      } catch (error) {
        if (isSignedOut(error)) endSession()
        else setRaceError(describeRaceFailure(error))
      } finally {
        setRaceRunning(false)
      }
    },
    [token, refreshSeats, endSession],
  )

  const reset = useCallback(async (): Promise<string | null> => {
    if (!token) return null

    try {
      await resetDemoData(token)
      // The hub tells everyone, this page included; without a connection the page would not hear of it.
      dataWasReset()
      return null
    } catch (error) {
      if (isSignedOut(error)) {
        endSession()
        return null
      }
      return describeResetFailure(error)
    }
  }, [token, dataWasReset, endSession])

  const retry = useCallback(() => {
    setLoad({ kind: 'loading' })
    setReloads((count) => count + 1)
  }, [])

  const dismissNotice = useCallback(() => setNotice(null), [])

  return {
    load,
    seats,
    bookings,
    live,
    notice,
    dismissNotice,
    retry,
    busySeats,
    busyBookings,
    seatError,
    bookingErrors,
    lastEvents,
    liveEvents,
    resyncs,
    race,
    raceRunning,
    raceError,
    startRace,
    resetDemoData: reset,
    hold,
    confirm,
    release,
  }
}

const NO_BOOKINGS: Booking[] = []

// More than a visit produces; the list must only not grow without end.
const MAX_LIVE_EVENTS = 200

function without(set: ReadonlySet<number>, value: number): ReadonlySet<number> {
  const next = new Set(set)
  next.delete(value)
  return next
}

function withoutKey(record: Record<number, string>, key: number): Record<number, string> {
  const { [key]: _removed, ...rest } = record
  return rest
}

function describeLoadFailure(error: unknown): string {
  if (error instanceof ApiError && error.code === NETWORK_ERROR) {
    return 'The server could not be reached. Check your connection, then try again.'
  }
  if (error instanceof ApiError && error.code === 'rate_limited') {
    return 'Too many requests in a short time. Wait a minute, then try again.'
  }
  return 'Something went wrong on the server. Try again.'
}
