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
*   **Booking Model:** A seat has no "booked" flag; whether it is taken follows from its bookings. A booking has a status (`Held`, `Confirmed`, `Expired`, `Released`) and timestamps. Today a booking is confirmed immediately; the hold step is on the roadmap.
*   **Error Responses:** Errors are `application/problem+json` with a machine-readable `code`: `seat_not_found` (404), `seat_already_booked` (409), `seat_locked` (409, another request is booking that seat right now), `validation_failed` (400), `invalid_token` (401), `email_already_registered` (409), `invalid_credentials` (401).
*   **Messaging:** After a successful booking the API publishes a `BookingCreatedEvent` to **RabbitMQ (MassTransit)**, which the Worker consumes.
*   **Authentication and Roles:** **JWT** authentication with two roles, `User` and `Admin`. The setup and simulation endpoints are Admin only. The admin account is created at startup from environment variables; without them no admin exists.
*   **Input Validation:** Registration requires a valid email and a password of at least 8 characters and at most 72 bytes. Emails are stored in lowercase and are unique.
*   **Rate Limiting:** Register and login are limited to 10 requests per minute per IP address, booking to 30 requests per minute per user. Requests over the limit get `429`.
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
5.  **Book:** `POST /api/booking` with `{ "seatId": 5 }` (requires a token). Returns `{ "bookingId", "seatId", "status": "Confirmed" }`.

## Concurrency Simulation

`POST /api/simulation/simulate-concurrency` (Admin only) starts 20 concurrent booking attempts for Seat #1 and reports how many succeeded. The expected result is 1 success and 19 rejections.

Its limits: it calls the booking service directly instead of going through HTTP and JWT, all 20 attempts are made as the admin who started it, it always uses Seat #1, and the seat stays booked afterwards, so run `create-data` again before repeating it.

## Tests

```bash
dotnet test
```

The tests need a running Docker daemon, because the integration tests start a real PostgreSQL and a real Redis with Testcontainers.

*   **Unit tests:** the booking service with a mocked lock and message bus, for example that a lock which was not acquired is never released.
*   **Concurrency (Testcontainers):** 20 concurrent requests for the same seat, repeated for 25 rounds, must produce exactly one booking each round. Further tests check that a lock can only be released by its owner, and that the database rejects a second active booking for the same seat even with the lock disabled.
*   **Authorization and API (WebApplicationFactory):** the real API runs in-process and is checked for role access (401/403), status codes and error codes, validation (400), duplicate emails, rate limiting (429) and startup configuration.
*   **Migrations:** every integration test runs on a schema built by the EF Core migrations, and one test upgrades a database from the first migration.

## Roadmap

Not implemented yet:

*   Seat hold with a TTL before payment (the `Held` status and `ExpiresAt` exist in the model but nothing creates or expires holds yet).
*   Simulated payment step.
*   Transactional outbox. Today the database write and the event publish are not atomic: if publishing fails, the seat is booked but no event is sent.
*   Live updates with SignalR.
*   A hosted live demo.
