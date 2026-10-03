import { describe, expect, it } from 'vitest'
import { clearSession, displayName, loadSession, readToken, saveSession } from './session'

// A token shaped like the API's: only the payload matters here, the signature is never read.
function tokenWith(claims: Record<string, unknown>): string {
  const encode = (value: unknown) =>
    btoa(String.fromCharCode(...new TextEncoder().encode(JSON.stringify(value))))
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '')
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(claims)}.signature`
}

const EXPIRES = 1_800_000_000
const guest = { sub: '42', email: 'guest-abc@guests.seathive.invalid', role: 'Guest', exp: EXPIRES }

function memoryStorage() {
  const items = new Map<string, string>()
  return {
    getItem: (key: string) => items.get(key) ?? null,
    setItem: (key: string, value: string) => void items.set(key, value),
    removeItem: (key: string) => void items.delete(key),
    size: () => items.size,
  }
}

describe('readToken', () => {
  it('reads the user, the role and the expiry', () => {
    const token = tokenWith(guest)

    expect(readToken(token)).toEqual({
      token,
      userId: 42,
      email: 'guest-abc@guests.seathive.invalid',
      role: 'Guest',
      expiresAt: EXPIRES * 1000,
    })
  })

  it('reads an email that is not ASCII', () => {
    expect(readToken(tokenWith({ ...guest, email: 'doğan@example.com', role: 'User' }))?.email).toBe('doğan@example.com')
  })

  it.each([
    ['not a token', 'nonsense'],
    ['a payload that is not JSON', 'a.b.c'],
    ['a user id that is not a number', tokenWith({ ...guest, sub: 'abc' })],
    ['an unknown role', tokenWith({ ...guest, role: 'Racer' })],
    ['no expiry', tokenWith({ sub: '42', email: 'a@b.c', role: 'User' })],
  ])('answers null for %s', (_reason, token) => {
    expect(readToken(token)).toBeNull()
  })
})

describe('loadSession', () => {
  it('answers the saved session while its token is valid', () => {
    const storage = memoryStorage()
    saveSession(readToken(tokenWith(guest))!, storage)

    expect(loadSession(storage, EXPIRES * 1000 - 1)?.userId).toBe(42)
  })

  it('forgets a session whose token has run out', () => {
    const storage = memoryStorage()
    saveSession(readToken(tokenWith(guest))!, storage)

    expect(loadSession(storage, EXPIRES * 1000)).toBeNull()
    expect(storage.size()).toBe(0)
  })

  it('forgets a stored value that is not a token', () => {
    const storage = memoryStorage()
    storage.setItem('seathive.token', 'nonsense')

    expect(loadSession(storage, 0)).toBeNull()
    expect(storage.size()).toBe(0)
  })

  it('answers null after the session was cleared', () => {
    const storage = memoryStorage()
    saveSession(readToken(tokenWith(guest))!, storage)
    clearSession(storage)

    expect(loadSession(storage, 0)).toBeNull()
  })
})

describe('displayName', () => {
  it('calls a guest "Guest" and a user by its email', () => {
    expect(displayName(readToken(tokenWith(guest))!)).toBe('Guest')
    expect(displayName(readToken(tokenWith({ ...guest, email: 'ada@example.com', role: 'User' }))!)).toBe('ada@example.com')
  })
})
