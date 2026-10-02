# SeatHive - Ticket Booking Backend

[![CI](https://github.com/wingtion/SeatHive/actions/workflows/ci.yml/badge.svg)](https://github.com/wingtion/SeatHive/actions/workflows/ci.yml)

SeatHive is a .NET backend for booking event seats. Its main job is to make sure that when many requests try to book the same seat at the same time, exactly one of them gets it.

This is a portfolio project. It is not deployed anywhere yet; see the [Roadmap](#roadmap) for what is still missing.

## Architecture

Two services and a shared contract library:

*   **API Service:** Handles HTTP requests, authentication and the booking logic.
*   **Worker Service:** Consumes `BookingCreatedEvent` from RabbitMQ and writes it to the log. It does not send emails or generate tickets yet.
*   **Shared Library:** Contains the event contract used by both services.

## Key Features

*   **Concurrency Control:** A seat booking is protected by two layers.
    1.  A Redis lock per seat: acquired with `SET NX` and a TTL, storing a random token. It is released by a Lua script that deletes the key only if it still holds that token, so a request can never release a lock it does not own.
    2.  A partial unique index in PostgreSQL: every booking is a row in the `Bookings` table, and the index allows at most one active (`Held` or `Confirmed`) booking per seat. A second insert for the same seat fails and the booking is rejected.

    This is not Redlock. Redlock coordinates locks across several independent Redis nodes; SeatHive runs a single Redis instance, and the database index is what finally guarantees correctness even if the lock fails or expires.
*   **Booking Model:** A seat has no "booked" flag; whether it is taken follows from its bookings. A booking has a status (`Held`, `Confirmed`, `Expired`, `Released`) and timestamps.
*   **Hold → Confirm Flow:** A seat is first held, then confirmed.
    1.  **Hold:** creates a `Held` booking that expires after a configurable time (5 minutes by default). Holding a seat you already hold returns the same hold and does not extend it. A user can hold at most 4 seats at the same time (configurable).
    2.  **Confirm:** only the owner can confirm, and only before the hold expires (`Held` → `Confirmed`). Confirming an already confirmed booking again returns it unchanged. There is no payment step yet; confirming is direct.
    3.  **Release:** the owner can give a hold up (`Held` → `Released`); the seat is free again at once.
*   **Hold Expiry:** A hold whose time has run out stops counting immediately: it cannot be confirmed and does not count towards the user's limit. It is marked `Expired` in two ways: lazily, when someone tries to hold that seat, inside the same transaction as the new hold; and by a background service that sweeps expired holds every 30 seconds (configurable). Confirm and expiry are each a single conditional update, so only one of them can win for a given hold.
*   **Error Responses:** Errors are `application/problem+json` with a machine-readable `code`:
    *   Seats: `seat_not_found` (404), `seat_already_booked` (409, the seat has a confirmed booking), `seat_held` (409, another user holds the seat), `seat_locked` (409, another request is working on that seat right now).
    *   Holds: `hold_limit_reached` (409), `hold_expired` (410), `hold_not_active` (409, confirming a released hold or releasing a booking that is not a hold), `booking_not_found` (404), `not_hold_owner` (403).
    *   General: `validation_failed` (400), `invalid_token` (401), `email_already_registered` (409), `invalid_credentials` (401).
*   **Messaging:** When a hold is confirmed the API publishes a `BookingCreatedEvent` to **RabbitMQ (MassTransit)**, which the Worker consumes. Holding and releasing publish nothing.
*   **Authentication and Roles:** **JWT** authentication with two roles, `User` and `Admin`. The setup and simulation endpoints are Admin only. The admin account is created at startup from environment variables; without them no admin exists.
*   **Input Validation:** Registration requires a valid email and a password of at least 8 characters and at most 72 bytes. Emails are stored in lowercase and are unique.
*   **Rate Limiting:** Register and login are limited to 10 requests per minute per IP address, the booking endpoints (hold, confirm and release together) to 30 requests per minute per user. Requests over the limit get `429`.
*   **Configuration:** Secrets (JWT key, database, Redis and RabbitMQ credentials) are not in the repository. The API does not start if one is missing.
*   **Containerization:** **Docker Compose** runs the API, Worker, Postgres, Redis and RabbitMQ. Database migrations are applied when the API starts.

## Tech Stack

*   **.NET 10 Web API**
*   **PostgreSQL** (EF Core migrations)
*   **Redis** (StackExchange.Redis)
*   **RabbitMQ** (MassTransit)
*   **xUnit, Moq & Testcontainers**
*   **Docker & Docker Compose**

## How to Run

1.  **Clone the repo:**
    ```bash
    git clone https://github.com/wingtion/SeatHive.git
    cd SeatHive
    ```

2.  **Create your `.env` file:**
    ```bash
    cp .env.example .env
    ```
    Fill in every value in `.env` (for example with `openssl rand -hex 32`). The file explains each one. Set `SEATHIVE_ADMIN_EMAIL` and `SEATHIVE_ADMIN_PASSWORD` if you want an admin account. `.env` is ignored by git.

3.  **Start everything:**
    ```bash
    docker compose up -d --build
    ```
    This also publishes the Postgres, Redis and RabbitMQ ports on `127.0.0.1` for local development (`docker-compose.override.yml`). On a server, keep those ports closed by using only the main file:
    ```bash
    docker compose -f docker-compose.yml up -d --build
    ```

4.  **Access Swagger:**
    Navigate to `http://localhost:8080/swagger` (Swagger is only enabled in the Development environment, which the compose file uses).

## Using the API

1.  **Register:** `POST /api/auth/register`
2.  **Login:** `POST /api/auth/login` -> Copy Token (valid for 2 hours).
3.  **Authorize:** Click the lock icon in Swagger -> Paste `Bearer <TOKEN>`.
4.  **Create demo data (Admin):** `POST /api/setup/create-data`. Log in with the admin account from your `.env` first. This recreates one event with 100 seats; users are kept.
5.  **Hold:** `POST /api/booking/hold` with `{ "seatId": 5 }` (requires a token). Returns `{ "bookingId", "seatId", "status": "Held", "expiresAt" }`.
6.  **Confirm:** `POST /api/booking/{bookingId}/confirm` before `expiresAt`. Returns `{ "bookingId", "seatId", "status": "Confirmed", "confirmedAt" }`. After the hold has expired it returns `410` with `hold_expired`.
7.  **Or release:** `POST /api/booking/{bookingId}/release` gives the hold up. Returns `{ "bookingId", "seatId", "status": "Released" }`.

The hold settings are in the `Holds` configuration section: `DurationSeconds` (300), `MaxActivePerUser` (4) and `SweepIntervalSeconds` (30).

## Concurrency Simulation

`POST /api/simulation/simulate-concurrency` (Admin only) starts 20 concurrent attempts to hold Seat #1 and reports how many of them took the seat. The expected result is 1 success and 19 failures.

Its limits: it calls the booking service directly instead of going through HTTP and JWT, all 20 attempts are made as the admin who started it, it always uses Seat #1, and it only holds the seat without confirming it. The hold stays until it expires, and while it lasts a repeated run reports 0 successes, so run `create-data` again before repeating it.

## Tests

```bash
dotnet test
```

The tests need a running Docker daemon, because the integration tests start a real PostgreSQL and a real Redis with Testcontainers.

*   **Booking service (Testcontainers):** the service on a real database, partly with a mocked lock and message bus: for example that a lock which was not acquired is never released, and that the event is published once on confirm and never on hold.
*   **Hold flow (Testcontainers):** the same user holding a seat twice gets the same hold; another user can take a seat once its hold has run out, without the sweeper; confirming or releasing someone else's hold is rejected; an expired hold cannot be confirmed; the per-user limit; the sweep expires only holds that ran out. Time comes from a fake clock that the tests advance, so nothing waits. The sweeper's timer loop itself is not tested, only the expiry it calls.
*   **Concurrency (Testcontainers):** 20 concurrent users holding the same seat, repeated for 25 rounds, must produce exactly one hold each round. One user holding 10 seats at once must end up with exactly 4. A confirm racing the sweeper must end either confirmed or expired, never mixed. Further tests check that a lock can only be released by its owner, and that the database rejects a second active booking for the same seat even with the lock disabled.
*   **Authorization and API (WebApplicationFactory):** the real API runs in-process and is checked for role access (401/403), status codes and error codes, validation (400), duplicate emails, rate limiting (429) and startup configuration.
*   **Migrations:** every integration test runs on a schema built by the EF Core migrations, and one test upgrades a database from the first migration.

## Roadmap

Not implemented yet:

*   Simulated payment step between hold and confirm.
*   Transactional outbox. Today the database write and the event publish are not atomic: if publishing fails, the booking is confirmed but no event is sent.
*   Live updates with SignalR.
*   A hosted live demo.
