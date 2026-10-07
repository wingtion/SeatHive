import { createContext, useContext } from 'react'
import type { Session } from './session'

export interface AuthValue {
  session: Session | null
  continueAsGuest: () => Promise<void>
  signIn: (email: string, password: string) => Promise<void>
  // Creates the account and signs in with it.
  register: (email: string, password: string) => Promise<void>
  signOut: () => void
}

export const AuthContext = createContext<AuthValue | null>(null)

export function useAuth(): AuthValue {
  const value = useContext(AuthContext)
  if (!value) throw new Error('useAuth must be used inside AuthProvider.')
  return value
}
