import { request } from './client'

// Admin only. Replaces every event, seat and booking with the demo data; users stay.
export async function resetDemoData(token: string): Promise<void> {
  await request<unknown>('/api/setup/create-data', { method: 'POST', token })
}
