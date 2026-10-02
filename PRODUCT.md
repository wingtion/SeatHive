# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

React + Vite + TypeScript single-page app in `/frontend` of this repo.

- Talks to the SeatHive API over HTTP with JWT bearer auth.
- Live seat updates over SignalR via `@microsoft/signalr`.
- Deployed as a static site on Netlify, not as a Docker container.
- The API, worker, Postgres, Redis and RabbitMQ stay in Docker Compose on the server.
- Local development runs the Vite dev server with a proxy to the API.

## Users

Primary: engineers and recruiters evaluating SeatHive as a portfolio piece. They arrive with little time and no context, and their job is to judge whether the author understands distributed-systems problems. They should understand the concurrency guarantees within one to two minutes.

Secondary: a fan booking a seat for a high-demand event. This flow is the stage the system is demonstrated on, so it has to be real and believable, not a mock-up.

## Product Purpose

SeatHive is a distributed, high-concurrency ticketing backend: many people compete for the same seat and exactly one of them gets it. It is a portfolio and learning project, with no real customers, events or payments.

The interface exists to make that backend's behaviour visible. Success means an evaluator watches a real booking happen and, next to it, sees why it was safe: who raced, who held the lock, who was rejected, and what happened asynchronously afterwards.

## Positioning

A working seat-booking flow with its own "under the hood" layer beside it. The claims are not described, they are observed live from the running system:

- the race simulation: 20 users attempt the same seat at once, exactly 1 succeeds and 19 are rejected;
- live lock TTL countdowns on seats being held;
- an async event timeline: booking → payment → notification, carried over RabbitMQ.

## Operating Context

- Evaluators typically arrive from a CV, GitHub profile or the README, and may never clone the repo or open Swagger.
- Today the only way to see the system work is Swagger at `http://localhost:8080/swagger` after `docker-compose up -d --build`, calling `POST /api/setup/create-data` then `POST /api/simulation/simulate-concurrency`.
- The whole backend runs from one Docker Compose file: API, worker, Postgres, Redis, RabbitMQ.

## Capabilities and Constraints

Implemented today:

- `POST /api/auth/register` and `POST /api/auth/login` (email + password, returns a JWT).
- `POST /api/booking/hold`, `POST /api/booking/{id}/confirm` and `POST /api/booking/{id}/release` (authenticated; the user ID is taken from the token, never from the request body).
- `POST /api/setup/create-data` (Admin only): clears events, seats and bookings and restarts their ids, keeps users, seeds one event with 100 seats (sections A and B, row 1, seats 1–50 each).
- `POST /api/simulation/simulate-concurrency` (Admin only): 20 concurrent attempts to hold seat #1, all as the admin who started it; returns total, successful and failed counts.
- Booking is hold then confirm. `POST /api/booking/hold` takes a Redis lock on `lock:seat:{id}` (single-instance `SET NX` with a 10 second expiry), inserts a `Held` booking in Postgres that expires after 5 minutes (configurable; at most 4 active holds per user), then releases the lock. `POST /api/booking/{id}/confirm` turns the owner's unexpired hold into `Confirmed` and publishes `BookingCreatedEvent` (SeatId, UserId, CreatedAt) through MassTransit/RabbitMQ. `POST /api/booking/{id}/release` gives the hold up. Holds that ran out are marked `Expired` on the next hold attempt for that seat and by a background sweeper.
- The worker consumes `BookingCreatedEvent` and simulates slow ticket/email processing with a 2 second delay; it only logs.
- Booking outcomes: success returns JSON with `bookingId`, `seatId`, `status` and, for a hold, `expiresAt` (for a confirm, `confirmedAt`). Errors are `application/problem+json` with a machine-readable `code`: `seat_not_found` (404), `seat_already_booked` (409), `seat_held` (409), `seat_locked` (409), `hold_limit_reached` (409), `hold_expired` (410), `hold_not_active` (409), `booking_not_found` (404), `not_hold_owner` (403), `validation_failed` (400), `invalid_token` (401).
- Data model: Event (name, date), Seat (section, row, seat number; no booked flag), Booking (seat, user, status `Held`/`Confirmed`/`Expired`/`Released`, created/expires/confirmed timestamps), User (email, password hash, role). A seat is taken while it has a `Held` or `Confirmed` booking; a partial unique index allows at most one of those per seat.

Planned backend work the interface depends on (confirmed direction, not yet built):

- seat holds with a real, user-visible TTL (today the lock lives only for the duration of one request);
- a simulated payment step between booking and notification;
- a SignalR hub for live seat and system events;
- read endpoints for events and seats;
- CORS for the Netlify origin.

Constraints:

- The interface shows only real system state. Until the planned backend work exists, the matching UI is not faked with client-side telemetry.
- Payment is simulated; no real payment provider.

Undecided:

- Whether the public deployment has an always-on backend to connect to, or is run locally by evaluators.
- How the destructive `create-data` reset is exposed, if at all, in a shared deployment.
- Simulation parameters beyond the fixed 20 users on seat #1.

The README now matches the code and can be used as the source for UI copy: the lock is a token-owned single-instance Redis lock (`SET NX`, released by a Lua script) backed by a partial unique index on active bookings, not Redlock, and the roles are `User` and `Admin`.

## Brand Commitments

- Name: SeatHive.
- No logo, visual identity or voice has been established.

## Evidence on Hand

- `README.md`: architecture, feature list, run and test instructions.
- The simulation endpoint's real output (1 success, 19 rejections).
- Unit tests in `SeatHive.Tests/BookingServiceTests.cs` (xUnit, Moq).
- Seed event: "Tarkan - Harbiye Open Air", 30 days out. This is sample data, not a real listing or partnership.

Absent, and not to be fabricated: customers, testimonials, real events or venues, pricing, sales figures, load or latency benchmarks, uptime claims, a seat map or venue layout beyond section/row/number.

## Product Principles

1. Show, don't claim. Every guarantee is demonstrated by the running system rather than asserted in copy.
2. Understood in two minutes. An evaluator with no context reaches the core concurrency story fast, without setup or reading documentation.
3. The booking flow is real. The stage must work as an actual product flow, or the layer beside it proves nothing.
4. Honest telemetry. What is displayed is what the backend did; simulated parts (payment, notification) are named as simulated.
5. Rejection is a result. The 19 who lose the race are the proof, and are presented as the system working.
