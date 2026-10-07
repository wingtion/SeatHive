import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, NETWORK_ERROR, UNKNOWN_ERROR, request } from './client'

function respondWith(status: number, body: unknown, contentType = 'application/json') {
  const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) =>
    new Response(typeof body === 'string' ? body : JSON.stringify(body), {
      status,
      headers: { 'Content-Type': contentType },
    }),
  )
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

async function failureOf(promise: Promise<unknown>): Promise<ApiError> {
  const error = await promise.then(
    () => null,
    (reason: unknown) => reason,
  )
  expect(error).toBeInstanceOf(ApiError)
  return error as ApiError
}

afterEach(() => vi.unstubAllGlobals())

describe('request', () => {
  it('returns the JSON body of a successful answer', async () => {
    respondWith(200, { token: 'abc' })

    expect(await request('/api/auth/guest', { method: 'POST' })).toEqual({ token: 'abc' })
  })

  it('sends the token and a JSON body', async () => {
    const fetchMock = respondWith(200, {})

    await request('/api/booking/hold', { method: 'POST', body: { seatId: 5 }, token: 'abc' })

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/booking/hold')
    expect(init?.method).toBe('POST')
    expect(init?.body).toBe('{"seatId":5}')
    expect(init?.headers).toMatchObject({ Authorization: 'Bearer abc', 'Content-Type': 'application/json' })
  })

  it('sends no Authorization header without a token', async () => {
    const fetchMock = respondWith(200, {})

    await request('/api/events')

    expect(fetchMock.mock.calls[0][1]?.headers).not.toHaveProperty('Authorization')
  })

  it('answers undefined for a successful answer that is not JSON', async () => {
    respondWith(200, 'User registered successfully.', 'text/plain')

    expect(await request('/api/auth/register', { method: 'POST', body: {} })).toBeUndefined()
  })

  it('turns a problem body into an ApiError with the code of the API', async () => {
    respondWith(
      409,
      { title: 'Seat is held by someone else.', status: 409, code: 'seat_held', traceId: 'x' },
      'application/problem+json',
    )

    const error = await failureOf(request('/api/booking/hold', { method: 'POST', body: { seatId: 5 } }))

    expect(error.status).toBe(409)
    expect(error.code).toBe('seat_held')
    expect(error.message).toBe('Seat is held by someone else.')
  })

  it('keeps the validation errors by field', async () => {
    respondWith(
      400,
      { status: 400, code: 'validation_failed', errors: { Email: ['The Email field is not a valid e-mail address.'] } },
      'application/problem+json',
    )

    const error = await failureOf(request('/api/auth/register', { method: 'POST', body: {} }))

    expect(error.code).toBe('validation_failed')
    expect(error.fieldErrors).toEqual({ Email: ['The Email field is not a valid e-mail address.'] })
  })

  it('keeps further members of the problem body', async () => {
    respondWith(
      409,
      { status: 409, code: 'seat_held_by_race', title: 'Held.', releasesAt: '2026-10-03T14:00:00Z' },
      'application/problem+json',
    )

    const error = await failureOf(request('/api/simulation/simulate-concurrency', { method: 'POST', body: {} }))

    expect(error.extensions).toEqual({ releasesAt: '2026-10-03T14:00:00Z' })
  })

  it('reports an error answer without a readable body as unknown', async () => {
    respondWith(502, '<html>Bad Gateway</html>', 'text/html')

    const error = await failureOf(request('/api/events'))

    expect(error.status).toBe(502)
    expect(error.code).toBe(UNKNOWN_ERROR)
  })

  it('reports a request that got no answer as a network error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('Failed to fetch')
      }),
    )

    const error = await failureOf(request('/api/events'))

    expect(error.status).toBe(0)
    expect(error.code).toBe(NETWORK_ERROR)
  })

  it('lets an aborted request fail as aborted, not as a network error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new DOMException('Aborted', 'AbortError')
      }),
    )

    await expect(request('/api/events')).rejects.toMatchObject({ name: 'AbortError' })
  })
})
