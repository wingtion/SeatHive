import { useEffect, useState } from 'react'
import { getBookingHistory } from '../api/bookings'
import type { TimelineEntry } from './hoodState'

export interface BookingHistoryState {
  // 'failed' when it could not be read; what the hub said is still shown.
  status: 'loading' | 'ready' | 'failed'
  entries: TimelineEntry[]
}

const NOTHING: TimelineEntry[] = []

// The recorded history of one booking, read when the booking is chosen and again after a gap in the connection.
// It follows the change by about a second; the caller adds what the hub said meanwhile.
export function useBookingHistory(token: string | null, bookingId: number | null, resyncs: number): BookingHistoryState {
  // Kept with what it was read for, so one booking's history is never shown under another.
  const [read, setRead] = useState<{ key: string; entries: TimelineEntry[] | null }>({ key: '', entries: null })
  const key = token !== null && bookingId !== null ? `${token}:${bookingId}` : ''

  useEffect(() => {
    if (token === null || bookingId === null) return

    const abort = new AbortController()
    getBookingHistory(token, bookingId, abort.signal)
      .then((page) => setRead({ key, entries: page.items }))
      .catch(() => {
        if (!abort.signal.aborted) setRead({ key, entries: null })
      })
    return () => abort.abort()
  }, [token, bookingId, key, resyncs])

  if (key === '' || read.key !== key) return { status: 'loading', entries: NOTHING }
  return read.entries === null ? { status: 'failed', entries: NOTHING } : { status: 'ready', entries: read.entries }
}
