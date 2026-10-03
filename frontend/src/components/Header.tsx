import { useState } from 'react'
import { ApiError, NETWORK_ERROR } from '../api/client'
import { useAuth } from '../auth/context'
import { displayName } from '../auth/session'
import { Button } from './Button'
import { SignInDialog } from './SignInDialog'

export function Header() {
  const { session, continueAsGuest, signOut } = useAuth()
  const [signInOpen, setSignInOpen] = useState(false)
  const [enteringAsGuest, setEnteringAsGuest] = useState(false)
  const [guestError, setGuestError] = useState<string | null>(null)

  async function enterAsGuest() {
    setEnteringAsGuest(true)
    setGuestError(null)

    try {
      await continueAsGuest()
    } catch (error) {
      setGuestError(describeGuestFailure(error))
    } finally {
      setEnteringAsGuest(false)
    }
  }

  return (
    <header className="border-b border-line bg-surface">
      <div className="mx-auto flex min-h-14 max-w-[1400px] flex-wrap items-center justify-between gap-x-6 gap-y-2 px-4 py-2 sm:px-6">
        <p className="flex items-center gap-2.5 text-base font-semibold tracking-tight">
          <span aria-hidden className="size-3.5 rounded-seat bg-hold" />
          SeatHive
        </p>

        {session ? (
          <div className="flex items-center gap-4">
            <p className="flex min-w-0 items-center gap-2 text-sm">
              <span className="truncate" title={session.email}>
                {displayName(session)}
              </span>
              {session.role === 'Admin' && (
                <span className="rounded-seat border border-line-strong px-1.5 py-px font-mono text-xs">admin</span>
              )}
            </p>
            <Button onClick={signOut}>Sign out</Button>
          </div>
        ) : (
          <div className="flex flex-wrap items-center justify-end gap-x-3 gap-y-2">
            {guestError && (
              <p role="alert" className="text-sm text-danger">
                {guestError}
              </p>
            )}
            <Button onClick={() => setSignInOpen(true)}>Sign in</Button>
            <Button variant="primary" onClick={enterAsGuest} disabled={enteringAsGuest}>
              {enteringAsGuest ? 'Entering' : 'Continue as guest'}
            </Button>
          </div>
        )}
      </div>

      <SignInDialog open={signInOpen} onClose={() => setSignInOpen(false)} />
    </header>
  )
}

function describeGuestFailure(error: unknown): string {
  if (error instanceof ApiError && error.code === 'rate_limited') return 'Too many attempts. Wait a minute.'
  if (error instanceof ApiError && error.code === NETWORK_ERROR) return 'The server could not be reached.'
  return 'Could not enter as a guest. Try again.'
}
