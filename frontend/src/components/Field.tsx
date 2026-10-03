import { useId, type InputHTMLAttributes } from 'react'

interface FieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string
  hint?: string
  error?: string
}

// Label above, hint and error below; the error replaces the hint and is announced with the input.
export function Field({ label, hint, error, ...input }: FieldProps) {
  const id = useId()
  const noteId = `${id}-note`
  const note = error ?? hint

  return (
    <div className="flex flex-col gap-2">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      <input
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={note ? noteId : undefined}
        className={
          'h-10 rounded-control border bg-surface px-3 text-base text-ink ' +
          (error ? 'border-danger' : 'border-line-strong')
        }
        {...input}
      />
      {note && (
        <p id={noteId} className={`text-sm ${error ? 'text-danger' : 'text-ink-muted'}`}>
          {note}
        </p>
      )}
    </div>
  )
}
