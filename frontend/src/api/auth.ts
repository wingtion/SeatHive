import { request } from './client'

interface TokenResponse {
  token: string
}

// A new guest: a user of its own that lasts as long as its token.
export async function createGuest(): Promise<string> {
  return (await request<TokenResponse>('/api/auth/guest', { method: 'POST' })).token
}

export async function login(email: string, password: string): Promise<string> {
  return (await request<TokenResponse>('/api/auth/login', { method: 'POST', body: { email, password } })).token
}

// Registering does not sign in; the caller logs in afterwards.
export async function register(email: string, password: string): Promise<void> {
  await request<void>('/api/auth/register', { method: 'POST', body: { email, password } })
}
