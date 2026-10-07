import { useId, useState, type FormEvent } from 'react'
import type { Seat } from '../api/events'
import { DEFAULT_RACERS, MAX_RACERS, MIN_RACERS, type RaceAttempt, type RaceReport } from '../api/simulation'
import { formatRemaining, parseUtc, seatLabel } from '../board/boardState'
import { attemptKind, countAttempts, type AttemptKind } from '../hood/hoodState'
import { Button } from './Button'

// How a racer is drawn: a tag, wider than it is high, so it is never taken for a seat of the map. The winner is
// the amber of a held seat, because it holds one. The others differ in border and fill, not in colour: turned
// away at the lock is a plain outline, refused by the database is dashed.
const racerStyles: Record<AttemptKind, string> = {
  won: 'border border-hold-text bg-hold font-semibold text-hold-ink',
  lock: 'border border-line-strong text-ink-muted',
  database: 'border border-dashed border-ink bg-ink/10 text-ink',
  failed: 'border-2 border-danger text-danger',
}

const kindNames: Record<AttemptKind, string> = {
  won: 'got the seat',
  lock: 'stopped at the lock',
  database: 'refused by the database',
  failed: 'failed',
}

interface RacePanelProps {
  signedIn: boolean
  seats: Seat[]
  race: RaceReport | null
  running: boolean
  error: string | null
  now: number
  onStart: (racers: number) => void
  // Entering as a guest, offered here to someone who is not signed in.
  entering: boolean
  enterError: string | null
  onEnter: () => void
}

// The race simulation: many racers, one seat, one moment. What comes back is the API's own report.
export function RacePanel({ signedIn, seats, race, running, error, now, onStart, entering, enterError, onEnter }: RacePanelProps) {
  const racersId = useId()
  const [racers, setRacers] = useState(String(DEFAULT_RACERS))
  const count = Number(racers)
  const valid = Number.isInteger(count) && count >= MIN_RACERS && count <= MAX_RACERS

  function start(event: FormEvent) {
    event.preventDefault()
    if (valid && !running) onStart(count)
  }

  return (
    <section aria-labelledby="race-heading" className="flex flex-col gap-4">
      <div className="flex flex-col gap-1.5">
        <h3 id="race-heading" className="text-lg font-semibold">
          Race for one seat
        </h3>
        <p className="max-w-[65ch] text-sm text-ink-muted">
          Every racer asks for the first free seat at the same moment, through the same code as your own hold.
        </p>
      </div>

      {signedIn ? (
        <form onSubmit={start} className="flex flex-wrap items-end gap-3">
          <div className="flex flex-col gap-2">
            <label htmlFor={racersId} className="text-sm font-medium">
              Racers
            </label>
            <input
              id={racersId}
              type="number"
              inputMode="numeric"
              min={MIN_RACERS}
              max={MAX_RACERS}
              value={racers}
              onChange={(event) => setRacers(event.target.value)}
              aria-invalid={valid ? undefined : true}
              aria-describedby={valid ? undefined : `${racersId}-note`}
              className={
                'h-9 w-20 rounded-control border bg-surface px-3 font-mono text-sm tabular-nums text-ink ' +
                (valid ? 'border-line-strong' : 'border-danger')
              }
            />
          </div>
          <Button type="submit" variant="primary" disabled={running || !valid}>
            {running ? 'Racing' : 'Start the race'}
          </Button>
          {!valid && (
            <p id={`${racersId}-note`} className="w-full text-sm text-danger">
              Between {MIN_RACERS} and {MAX_RACERS} racers.
            </p>
          )}
        </form>
      ) : (
        // The one place the page asks for it: a race is the first thing to try, and it needs a guest.
        <div className="flex flex-col items-start gap-3">
          <p className="text-sm">A race needs a guest. Entering takes one click and no account.</p>
          <Button variant="primary" disabled={entering} onClick={onEnter}>
            {entering ? 'Entering' : 'Continue as guest'}
          </Button>
          {enterError && (
            <p role="alert" className="text-sm text-danger">
              {enterError}
            </p>
          )}
        </div>
      )}

      {error && (
        <p role="alert" className="border-l-2 border-ink pl-3 text-sm">
          {error}
        </p>
      )}

      {race ? (
        <RaceResult race={race} seats={seats} now={now} />
      ) : (
        signedIn && !error && <p className="text-sm text-ink-muted">No race yet. Its report appears here, for everyone watching.</p>
      )}
    </section>
  )
}

function RaceResult({ race, seats, now }: { race: RaceReport; seats: Seat[]; now: number }) {
  const seat = seats.find((candidate) => candidate.seatId === race.seatId)
  const label = seat ? seatLabel(seat) : `seat ${race.seatId}`
  const counts = countAttempts(race.attempts)
  const kinds: AttemptKind[] = counts.failed > 0 ? ['won', 'lock', 'database', 'failed'] : ['won', 'lock', 'database']

  return (
    <div className="flex flex-col gap-4">
      {/* The result in one sentence, the count of winners large: that number is the claim. */}
      <p className="flex flex-wrap items-baseline gap-x-3 gap-y-1" aria-live="polite">
        <span className="font-mono text-5xl leading-none font-medium tabular-nums">{race.winners}</span>
        <span className="text-base">
          of {race.racers} racers got seat <span className="font-semibold">{label}</span>, in{' '}
          <span className="font-mono tabular-nums">{race.durationMs}</span> ms
        </span>
      </p>

      {race.winners > 1 && (
        <p role="alert" className="border-l-2 border-danger pl-3 text-sm text-danger">
          More than one racer got the seat. That must never happen.
        </p>
      )}

      <div className="flex flex-col gap-2">
        <p id="racers-caption" className="text-sm text-ink-muted">
          The racers, by number:
        </p>
        <ul className="flex max-w-xl flex-wrap gap-1.5" aria-labelledby="racers-caption">
          {race.attempts.map((attempt) => {
            const kind = attemptKind(attempt)
            return (
              <li
                key={attempt.racer}
                className={`flex h-6 min-w-9 items-center justify-center rounded-seat px-1.5 font-mono text-xs tabular-nums ${racerStyles[kind]}`}
              >
                <span className="sr-only">Racer </span>
                {attempt.racer}
                <span className="sr-only">, {kindNames[kind]}</span>
              </li>
            )
          })}
        </ul>
      </div>

      <ul className="flex flex-wrap gap-x-6 gap-y-2 text-sm">
        {kinds.map((kind) => (
          <li key={kind} className="flex items-center gap-2">
            <span aria-hidden className={`h-3 w-4.5 shrink-0 rounded-seat ${racerStyles[kind]}`} />
            <span className="font-mono font-medium tabular-nums">{counts[kind]}</span>
            <span className="text-ink-muted">{kindNames[kind]}</span>
          </li>
        ))}
      </ul>

      <Winner race={race} label={label} now={now} />

      <details className="text-sm">
        <summary className="w-fit cursor-pointer rounded-control font-medium underline decoration-line-strong underline-offset-4 hover:decoration-ink">
          Every attempt
        </summary>
        <div className="mt-3 max-h-72 max-w-xl overflow-auto" tabIndex={0} role="region" aria-label="Every attempt of the race">
          <table className="w-full border-collapse font-mono text-xs tabular-nums">
            <thead className="sticky top-0 bg-bg text-left font-sans text-ink-muted">
              <tr>
                <th scope="col" className="py-1.5 pr-3 font-medium">Racer</th>
                <th scope="col" className="py-1.5 pr-3 font-medium">Result</th>
                <th scope="col" className="py-1.5 pr-3 font-medium">Lock</th>
                <th scope="col" className="py-1.5 pr-3 font-medium">Code</th>
                <th scope="col" className="py-1.5 pr-3 text-right font-medium">From (ms)</th>
                <th scope="col" className="py-1.5 text-right font-medium">To (ms)</th>
              </tr>
            </thead>
            <tbody>
              {winnerFirst(race.attempts).map((attempt) => (
                <tr key={attempt.racer} className={attempt.outcome === 'won' ? 'font-semibold text-hold-text' : ''}>
                  <td className="py-1 pr-3">{attempt.racer}</td>
                  <td className="py-1 pr-3">{attempt.outcome}</td>
                  <td className="py-1 pr-3">{attempt.lock ?? 'none'}</td>
                  <td className="py-1 pr-3">{attempt.code ?? ''}</td>
                  <td className="py-1 pr-3 text-right">{attempt.startedAtMs}</td>
                  <td className="py-1 text-right">{attempt.finishedAtMs}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </div>
  )
}

// The winner on top, the others in the order of the report: the row looked for first is never below the fold.
function winnerFirst(attempts: RaceAttempt[]): RaceAttempt[] {
  return [...attempts.filter((a) => a.outcome === 'won'), ...attempts.filter((a) => a.outcome !== 'won')]
}

// The winner keeps the seat for a few seconds so the hold can be seen, then releases it like anyone would.
function Winner({ race, label, now }: { race: RaceReport; label: string; now: number }) {
  if (!race.winner) return <p className="text-sm text-ink-muted">Nobody got the seat.</p>

  const remaining = parseUtc(race.winner.releasesAt) - now
  if (remaining <= 0) {
    return (
      <p className="text-sm text-ink-muted">
        Racer {race.winner.racer} has let {label} go again.
      </p>
    )
  }

  return (
    <p className="text-sm text-hold-text">
      Racer {race.winner.racer} holds {label} for{' '}
      <span className="font-mono font-medium tabular-nums" aria-live="off">
        {formatRemaining(remaining)}
      </span>
      , then lets it go.
    </p>
  )
}
