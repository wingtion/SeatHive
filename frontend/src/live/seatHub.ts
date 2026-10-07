import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { apiBaseUrl } from '../api/client'
import type { RaceReport } from '../api/simulation'
import type { SeatChange } from '../board/boardState'

export type LiveStatus = 'off' | 'connecting' | 'live' | 'reconnecting'

// Something happened to one of the person's own bookings (the API's bookingEvent).
export interface BookingEventMessage {
  eventId: string
  bookingId: number
  seatId: number
  type: string
  occurredAt: string
  paymentId: string | null
  detail: string | null
  simulated: boolean
}

export interface SeatHubHandlers {
  onStatus: (status: LiveStatus) => void
  onSeatChanged: (change: SeatChange) => void
  onBookingEvent: (message: BookingEventMessage) => void
  // A race of the simulation on a seat of this event is over, whoever started it.
  onRaceFinished: (report: RaceReport) => void
  onDemoDataReset: () => void
  // The connection is back after a gap: whatever was sent meanwhile was missed, so everything is read again.
  onResync: () => void
}

// Watches one event on the API's hub until the returned function is called. The hub needs a token:
// a browser cannot set a header on a WebSocket, so the client sends it as the access_token query parameter.
export function watchEvent(token: string, eventId: number, handlers: SeatHubHandlers): () => void {
  let stopped = false

  const connection: HubConnection = new HubConnectionBuilder()
    .withUrl(`${apiBaseUrl}/hubs/seats`, { accessTokenFactory: () => token })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Error)
    .build()

  connection.on('seatStatusChanged', (message: SeatChange & { eventId: number }) => {
    if (message.eventId === eventId) handlers.onSeatChanged(message)
  })
  connection.on('bookingEvent', handlers.onBookingEvent)
  connection.on('demoDataReset', handlers.onDemoDataReset)
  connection.on('raceFinished', (report: RaceReport) => {
    if (report.eventId === eventId) handlers.onRaceFinished(report)
  })

  async function join() {
    await connection.invoke('JoinEvent', eventId)
    if (!stopped) handlers.onStatus('live')
  }

  connection.onreconnecting(() => {
    if (!stopped) handlers.onStatus('reconnecting')
  })
  connection.onreconnected(() => {
    if (stopped) return
    void join()
      .then(handlers.onResync)
      .catch(() => handlers.onStatus('off'))
  })
  connection.onclose(() => {
    if (!stopped) handlers.onStatus('off')
  })

  handlers.onStatus('connecting')
  const started = connection
    .start()
    .then(() => (stopped ? undefined : join()))
    .catch(() => {
      if (!stopped) handlers.onStatus('off')
    })

  return () => {
    stopped = true
    // Stopping in the middle of starting is reported by the client as an error; the start is let finish first.
    void started.then(() => {
      if (connection.state !== HubConnectionState.Disconnected) return connection.stop()
    })
  }
}
