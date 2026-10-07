// One place that talks HTTP to the SeatHive API. Every failure becomes an ApiError with the API's own
// machine-readable code ("seat_held", "rate_limited", ...), which is what the interface decides on.

// Empty in development (the Vite dev server proxies /api and /hubs); the API's HTTPS address in a deployed build.
export const apiBaseUrl = (import.meta.env?.VITE_API_BASE_URL ?? '').replace(/\/+$/, '')

// Codes the API does not send: made here when there is no usable answer.
export const NETWORK_ERROR = 'network_error'
export const UNKNOWN_ERROR = 'unknown_error'

export class ApiError extends Error {
  // 0 when no response arrived at all.
  readonly status: number
  readonly code: string
  // Validation errors by field name as the API names it ("Email", "Password").
  readonly fieldErrors: Record<string, string[]>
  // Further members of the problem body, such as "releasesAt".
  readonly extensions: Record<string, unknown>

  constructor(
    status: number,
    code: string,
    title: string,
    fieldErrors: Record<string, string[]> = {},
    extensions: Record<string, unknown> = {},
  ) {
    super(title)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.fieldErrors = fieldErrors
    this.extensions = extensions
  }
}

export interface RequestOptions {
  method?: 'GET' | 'POST'
  body?: unknown
  token?: string | null
  signal?: AbortSignal
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (options.token) headers.Authorization = `Bearer ${options.token}`

  let response: Response
  try {
    response = await fetch(apiBaseUrl + path, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    })
  } catch (error) {
    // Aborting is the caller's own doing, not a failure of the network.
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError(0, NETWORK_ERROR, 'The server could not be reached.')
  }

  const body = await readBody(response)
  if (!response.ok) throw toApiError(response.status, body)

  return body as T
}

// JSON where the API sent JSON (a problem body included), otherwise nothing: a few answers are plain text.
async function readBody(response: Response): Promise<unknown> {
  const type = response.headers.get('Content-Type') ?? ''
  if (!type.includes('json')) return undefined

  try {
    return await response.json()
  } catch {
    return undefined
  }
}

function toApiError(status: number, body: unknown): ApiError {
  if (typeof body !== 'object' || body === null) {
    return new ApiError(status, UNKNOWN_ERROR, `The server answered ${status}.`)
  }

  const { code, title, errors, type: _type, status: _status, traceId: _traceId, detail: _detail, ...extensions } =
    body as Record<string, unknown>

  return new ApiError(
    status,
    typeof code === 'string' ? code : UNKNOWN_ERROR,
    typeof title === 'string' ? title : `The server answered ${status}.`,
    isFieldErrors(errors) ? errors : {},
    extensions,
  )
}

function isFieldErrors(value: unknown): value is Record<string, string[]> {
  return (
    typeof value === 'object' &&
    value !== null &&
    Object.values(value).every((messages) => Array.isArray(messages) && messages.every((m) => typeof m === 'string'))
  )
}
