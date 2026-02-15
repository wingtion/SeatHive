# SeatHive - Distributed High-Concurrency Ticketing System

SeatHive is a backend solution for handling high-demand event ticket bookings. It addresses common distributed system challenges like **Race Conditions**, **Data Consistency**, and **System Scalability**.

## Architecture

The system is designed as a Microservices architecture:
*   **API Service:** Handles HTTP requests and booking logic.
*   **Worker Service:** Processes background tasks (Email/PDF generation) asynchronously.
*   **Shared Library:** Contains common contracts and events.

## Key Features

*   **Concurrency Control:** Solved the "Double-Booking" problem using **Redis Distributed Locking** (Redlock algorithm).
*   **Event-Driven Architecture:** Decoupled the Booking API from notification logic using **RabbitMQ (MassTransit)**.
*   **Zero-Trust Security:** Implemented **JWT Authentication** and Role-Based Access Control (RBAC).
*   **Automated Testing:** Achieved high test coverage using **xUnit** and **Moq** for core business logic.
*   **Containerization:** Fully Dockerized environment using **Docker Compose** (API, Worker, Postgres, Redis, RabbitMQ).

## 🛠️ Tech Stack

*   **.NET 8 Web API**
*   **PostgreSQL** (EF Core Code-First)
*   **Redis** (StackExchange.Redis)
*   **RabbitMQ** (MassTransit)
*   **xUnit & Moq**
*   **Docker & Docker Compose**

## How to Run

1.  **Clone the repo:**
    ```bash
    git clone https://github.com/YOUR_USERNAME/SeatHive.git
    cd SeatHive
    ```

2.  **Start Infrastructure:**
    ```bash
    docker-compose up -d --build
    ```

3.  **Access Swagger:**
    Navigate to `http://localhost:8080/swagger`

## Testing Concurrency

I included a simulation endpoint to prove the system works under load.

1.  **Reset DB:** `POST /api/setup/create-data`
2.  **Run Simulation:** `POST /api/simulation/simulate-concurrency`
    *   *Scenario:* 20 users try to book Seat #1 simultaneously.
    *   *Result:* Only **1** succeeds. **19** are rejected.

## Security (JWT)

1.  **Register:** `POST /api/auth/register`
2.  **Login:** `POST /api/auth/login` -> Copy Token.
3.  **Authorize:** Click the Green Lock icon in Swagger -> Paste `Bearer <TOKEN>`.
4.  **Book:** `POST /api/booking` (Requires Auth).
