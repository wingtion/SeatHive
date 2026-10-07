// Who is signed in, as the token says it. The token is the only thing kept; everything else is read from it.

export type Role = 'User' | 'Admin' | 'Guest'

export interface Session {
  token: string
  userId: number
  email: string
  role: Role
  // Milliseconds since the epoch.
  expiresAt: number
}

const STORAGE_KEY = 'seathive.token'
const ROLES: readonly string[] = ['User', 'Admin', 'Guest']

// The session a token stands for, or null when it is not a token of this API.
// Nothing is verified here: the API checks the signature on every request.
export function readToken(token: string): Session | null {
  const payload = token.split('.')[1]
  if (!payload) return null

  try {
    const claims = JSON.parse(decodeBase64Url(payload)) as Record<string, unknown>
    const userId = Number(claims.sub)
    if (
      !Number.isInteger(userId) ||
      typeof claims.email !== 'string' ||
      typeof claims.role !== 'string' ||
      !ROLES.includes(claims.role) ||
      typeof claims.exp !== 'number'
    ) {
      return null
    }

    return { token, userId, email: claims.email, role: claims.role as Role, expiresAt: claims.exp * 1000 }
  } catch {
    return null
  }
}

function decodeBase64Url(value: string): string {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/')
  const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=')
  const bytes = Uint8Array.from(atob(padded), (character) => character.charCodeAt(0))
  return new TextDecoder().decode(bytes)
}

type TokenStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

// The stored session, unless there is none, it is unreadable or it has run out; those are removed.
export function loadSession(storage: TokenStorage = localStorage, now: number = Date.now()): Session | null {
  const token = storage.getItem(STORAGE_KEY)
  if (!token) return null

  const session = readToken(token)
  if (!session || session.expiresAt <= now) {
    storage.removeItem(STORAGE_KEY)
    return null
  }

  return session
}

export function saveSession(session: Session, storage: TokenStorage = localStorage): void {
  storage.setItem(STORAGE_KEY, session.token)
}

export function clearSession(storage: TokenStorage = localStorage): void {
  storage.removeItem(STORAGE_KEY)
}

// A guest has no email of its own to show; the address the API made for it means nothing to the visitor.
export function displayName(session: Session): string {
  return session.role === 'Guest' ? 'Guest' : session.email
}
