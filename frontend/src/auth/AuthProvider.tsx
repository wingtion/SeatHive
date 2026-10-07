import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import * as authApi from '../api/auth'
import { AuthContext, type AuthValue } from './context'
import { clearSession, loadSession, readToken, saveSession, type Session } from './session'

// setTimeout takes at most a 32-bit number of milliseconds (about 24 days).
const MAX_TIMEOUT = 2 ** 31 - 1

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(() => loadSession())

  const accept = useCallback((token: string) => {
    const next = readToken(token)
    if (!next) throw new Error('The server sent a token that could not be read.')

    saveSession(next)
    setSession(next)
  }, [])

  const signOut = useCallback(() => {
    clearSession()
    setSession(null)
  }, [])

  // A token that has run out is refused by the API anyway; signing out at that moment says so up front.
  useEffect(() => {
    if (!session) return

    const remaining = session.expiresAt - Date.now()
    const timer = setTimeout(signOut, Math.min(Math.max(remaining, 0), MAX_TIMEOUT))
    return () => clearTimeout(timer)
  }, [session, signOut])

  const value = useMemo<AuthValue>(
    () => ({
      session,
      continueAsGuest: async () => accept(await authApi.createGuest()),
      signIn: async (email, password) => accept(await authApi.login(email, password)),
      register: async (email, password) => {
        await authApi.register(email, password)
        accept(await authApi.login(email, password))
      },
      signOut,
    }),
    [session, accept, signOut],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
