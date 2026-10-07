import type { Booking } from '../api/bookings'
import type { Session } from '../auth/session'
import type { EventBoard } from '../board/useEventBoard'
import { holdsNow } from '../hood/hoodState'
import { HoldsList } from './HoldsList'
import { RacePanel } from './RacePanel'
import { ResetDemoData } from './ResetDemoData'
import { Timeline } from './Timeline'

interface HoodProps {
  board: EventBoard
  session: Session | null
  // The person's active bookings by seat.
  mine: Map<number, Booking>
  now: number
}

// The hood: beside the booking, the evidence of why it was safe. Everything here is what the API reported.
export function Hood({ board, session, mine, now }: HoodProps) {
  return (
    <section aria-labelledby="hood-heading" className="flex flex-col gap-8">
      <div className="flex flex-col gap-2">
        <h2 id="hood-heading" className="scroll-mt-6 text-2xl font-semibold tracking-tight">
          Under the hood
        </h2>
        <p className="max-w-[65ch] text-ink-muted">
          Many people ask for one seat at the same moment, and exactly one gets it. Start a race to watch that happen, then
          hold and confirm a seat to follow what comes after.
        </p>
      </div>

      <RacePanel
        signedIn={session !== null}
        seats={board.seats}
        race={board.race}
        running={board.raceRunning}
        error={board.raceError}
        now={now}
        onStart={(racers) => void board.startRace(racers)}
      />

      <div className="border-t border-line pt-8">
        <HoldsList holds={holdsNow(board.seats, mine, board.race, now)} now={now} />
      </div>

      <div className="border-t border-line pt-8">
        <Timeline
          token={session?.token ?? null}
          bookings={board.bookings}
          liveEvents={board.liveEvents}
          resyncs={board.resyncs}
        />
      </div>

      {session?.role === 'Admin' && (
        <div className="border-t border-line pt-8">
          <ResetDemoData onReset={board.resetDemoData} />
        </div>
      )}
    </section>
  )
}
