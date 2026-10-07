import { request } from './client'

export interface Page<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

export interface EventSummary {
  id: number
  name: string
  date: string
  seatCount: number
}

export type SeatStatus = 'available' | 'held' | 'booked'

// What everyone may know about a seat: whether it is taken and until when, never by whom.
export interface Seat {
  seatId: number
  section: string
  row: string
  seatNumber: number
  status: SeatStatus
  heldUntil: string | null
}

// The most seats one request may ask for; the demo event has 100.
const MAX_SEAT_PAGE = 500

export function getEvents(signal?: AbortSignal): Promise<Page<EventSummary>> {
  return request<Page<EventSummary>>('/api/events', { signal })
}

export function getSeats(eventId: number, signal?: AbortSignal): Promise<Page<Seat>> {
  return request<Page<Seat>>(`/api/events/${eventId}/seats?pageSize=${MAX_SEAT_PAGE}`, { signal })
}
