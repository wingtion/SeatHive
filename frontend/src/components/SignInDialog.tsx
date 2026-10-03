import { X } from '@phosphor-icons/react'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useAuth } from '../auth/context'
import { describeAuthFailure, type AuthFailure } from '../auth/messages'
import { Button } from './Button'
import { Field } from './Field'

type Mode = 'signIn' | 'register'

const copy: Record<Mode, { title: string; submit: string; busy: string; switchPrompt: string; switchAction: string }> = {
  signIn: {
    title: 'Sign in',
    submit: 'Sign in',
    busy: 'Signing in',
    switchPrompt: 'No account yet?',
    switchAction: 'Create one',
  },
  register: {
    title: 'Create an account',
    submit: 'Create account',
    busy: 'Creating account',
    switchPrompt: 'Already have an account?',
    switchAction: 'Sign in',
  },
}

interface SignInDialogProps {
  open: boolean
  onClose: () => void
}

export function SignInDialog({ open, onClose }: SignInDialogProps) {
  const dialog = useRef<HTMLDialogElement>(null)
  const { signIn, register } = useAuth()
  const [mode, setMode] = useState<Mode>('signIn')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<AuthFailure>({})

  // The native dialog brings the focus trap, Escape and the backdrop with it.
  useEffect(() => {
    const element = dialog.current
    if (!element) return

    if (open && !element.open) element.showModal()
    if (!open && element.open) element.close()
  }, [open])

  function switchMode() {
    setMode(mode === 'signIn' ? 'register' : 'signIn')
    setFailure({})
  }

  function close() {
    setPassword('')
    setFailure({})
    onClose()
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setFailure({})

    try {
      await (mode === 'signIn' ? signIn(email, password) : register(email, password))
      setEmail('')
      close()
    } catch (error) {
      setFailure(describeAuthFailure(error))
    } finally {
      setBusy(false)
    }
  }

  const text = copy[mode]

  return (
    <dialog
      ref={dialog}
      onClose={close}
      aria-labelledby="sign-in-title"
      className="m-auto w-[min(26rem,calc(100vw-2rem))] rounded-control border border-line bg-surface p-0 text-ink backdrop:bg-ink/40"
    >
      <form onSubmit={submit} noValidate className="flex flex-col gap-5 p-6">
        <div className="flex items-start justify-between gap-4">
          <h2 id="sign-in-title" className="text-lg font-semibold">
            {text.title}
          </h2>
          <button
            type="button"
            onClick={close}
            aria-label="Close"
            className="-m-1.5 rounded-control p-1.5 text-ink-muted hover:bg-ink/5 hover:text-ink"
          >
            <X size={18} weight="bold" aria-hidden />
          </button>
        </div>

        {failure.form && (
          <p role="alert" className="border-l-2 border-danger pl-3 text-sm text-danger">
            {failure.form}
          </p>
        )}

        <Field
          label="Email"
          type="email"
          name="email"
          autoComplete="email"
          required
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          error={failure.email}
        />
        <Field
          label="Password"
          type="password"
          name="password"
          autoComplete={mode === 'signIn' ? 'current-password' : 'new-password'}
          required
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          hint={mode === 'register' ? '8 characters or more.' : undefined}
          error={failure.password}
        />

        <Button type="submit" variant="primary" disabled={busy} className="h-10">
          {busy ? text.busy : text.submit}
        </Button>

        <p className="text-sm text-ink-muted">
          {text.switchPrompt}{' '}
          <Button variant="quiet" onClick={switchMode} className="h-auto">
            {text.switchAction}
          </Button>
        </p>
      </form>
    </dialog>
  )
}
