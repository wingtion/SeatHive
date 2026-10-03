import { useState } from 'react'
import { Button } from './Button'

// Admin only. Resetting takes two steps, because it removes everyone's bookings at once.
export function ResetDemoData({ onReset }: { onReset: () => Promise<string | null> }) {
  const [step, setStep] = useState<'idle' | 'confirming' | 'resetting'>('idle')
  const [error, setError] = useState<string | null>(null)

  async function reset() {
    setStep('resetting')
    setError(await onReset())
    setStep('idle')
  }

  return (
    <section aria-labelledby="reset-heading" className="flex flex-col gap-4">
      <div className="flex flex-col gap-1.5">
        <h3 id="reset-heading" className="text-lg font-semibold">
          Demo data
        </h3>
        <p className="max-w-[65ch] text-sm text-ink-muted">
          Shown to admins only. The same reset runs by itself once a night.
        </p>
      </div>

      {step === 'idle' ? (
        <div>
          <Button onClick={() => setStep('confirming')}>Reset demo data</Button>
        </div>
      ) : (
        <div role="group" aria-labelledby="reset-question" className="flex flex-col gap-3 border-l-2 border-ink pl-4">
          <p id="reset-question" className="max-w-[65ch] text-sm">
            This deletes every booking and frees every seat, for everyone who is watching. It cannot be undone.
          </p>
          <div className="flex flex-wrap gap-2">
            <Button variant="primary" disabled={step === 'resetting'} onClick={() => void reset()}>
              {step === 'resetting' ? 'Resetting' : 'Delete and reset'}
            </Button>
            <Button autoFocus disabled={step === 'resetting'} onClick={() => setStep('idle')}>
              Keep the data
            </Button>
          </div>
        </div>
      )}

      {error && (
        <p role="alert" className="text-sm text-danger">
          {error}
        </p>
      )}
    </section>
  )
}
