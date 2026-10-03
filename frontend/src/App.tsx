import { AuthProvider } from './auth/AuthProvider'
import { EventOverview } from './components/EventOverview'
import { Header } from './components/Header'

export default function App() {
  return (
    <AuthProvider>
      <Header />
      <main className="mx-auto max-w-[1400px] px-4 py-8 sm:px-6 sm:py-10">
        <EventOverview />
      </main>
    </AuthProvider>
  )
}
