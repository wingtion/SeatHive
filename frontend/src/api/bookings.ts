import { request } from './client'
import type { Page } from './events'

export type BookingStatus = 'held' | 'paymentPending' | 'confirmed' | 'expired' | 'released'

export interface Booking {
  bookingId: number
  status: BookingStatus
  createdAt: string
  expiresAt: string | null
  confirmedAt: string | null
  seat: { seatId: number; section: string; row: string; seatNumber: number }
  event: { id: number; name: string; date: string }
}

export interface HoldResult {
  bookingId: number
  seatId: number
  status: BookingStatus
  expiresAt: string | null
}

// The caller's own bookings, newest first. The first page is enough: at most 4 are active at a time.
export function getMyBookings(token: string, signal?: AbortSignal): Promise<Page<Booking>> {
  return request<Page<Booking>>('/api/booking?pageSize=100', { token, signal })
}

export function holdSeat(token: string, seatId: number): Promise<HoldResult> {
  return request<HoldResult>('/api/booking/hold', { method: 'POST', body: { seatId }, token })
}

// Starts the payment. The booking is confirmed when the payment result arrives, a few seconds later.
// simulatePaymentFailure is the API's demo switch: that payment fails for certain.
export function confirmBooking(token: string, bookingId: number, simulatePaymentFailure: boolean): Promise<void> {
  return request<void>(`/api/booking/${bookingId}/confirm`, {
    method: 'POST',
    body: { simulatePaymentFailure },
    token,
  })
}

export function releaseBooking(token: string, bookingId: number): Promise<void> {
  return request<void>(`/api/booking/${bookingId}/release`, { method: 'POST', token })
}
