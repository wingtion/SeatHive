import { ApiError, NETWORK_ERROR } from '../api/client'

export interface AuthFailure {
  // Shown above the form.
  form?: string
  // Shown under the field.
  email?: string
  password?: string
}

// What to tell the person, from the API's error code. The API's own validation messages are shown as they are.
export function describeAuthFailure(error: unknown): AuthFailure {
  if (!(error instanceof ApiError)) return { form: 'Something went wrong. Try again.' }

  switch (error.code) {
    case 'invalid_credentials':
      return { form: 'Email or password is wrong.' }
    case 'email_already_registered':
      return { email: 'This email already has an account. Sign in instead.' }
    case 'validation_failed':
      return {
        email: error.fieldErrors.Email?.join(' '),
        password: error.fieldErrors.Password?.join(' '),
        form: error.fieldErrors.Email || error.fieldErrors.Password ? undefined : 'Check what you entered.',
      }
    case 'rate_limited':
      return { form: 'Too many attempts. Wait a minute, then try again.' }
    case NETWORK_ERROR:
      return { form: 'The server could not be reached. Check your connection, then try again.' }
    default:
      return { form: 'Something went wrong on the server. Try again.' }
  }
}
