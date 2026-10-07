import type { ButtonHTMLAttributes } from 'react'

type Variant = 'primary' | 'secondary' | 'quiet'

const base =
  'inline-flex h-9 items-center justify-center gap-2 rounded-control px-3.5 text-sm font-medium whitespace-nowrap ' +
  'transition-[background-color,border-color,transform] duration-150 active:translate-y-px ' +
  'disabled:cursor-not-allowed disabled:opacity-60 disabled:active:translate-y-0'

const variants: Record<Variant, string> = {
  // Ink on the page colour: 16:1 in both themes.
  primary: 'bg-ink text-bg hover:bg-ink/85',
  secondary: 'border border-line-strong text-ink hover:bg-ink/5',
  quiet: 'text-ink underline decoration-line-strong underline-offset-4 hover:decoration-ink px-0',
}

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant
}

export function Button({ variant = 'secondary', type = 'button', className = '', ...props }: ButtonProps) {
  return <button type={type} className={`${base} ${variants[variant]} ${className}`} {...props} />
}
