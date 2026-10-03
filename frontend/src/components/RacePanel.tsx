import { useId, useState, type FormEvent } from 'react'
import type { Seat } from '../api/events'
import { DEFAULT_RACERS, MAX_RACERS, MIN_RACERS, type RaceReport } from '../api/simulation'
import { formatRemaining, parseUtc, seatLabel } from '../board/boardState'
import { attemptKind, countAttempts, type AttemptKind } from '../hood/hoodState'
import { Button } from './Button'

// How a racer is drawn. The winner is the amber of a held seat, because it holds one. The others differ in
// border and fill, not in colour: turned away at the lock is a plain outline, refused by the database is dashed.
const racerStyles: Record<AttemptKind, string> = {
  won: 'border border-hold-text bg-hold font-semibold text-hold-ink',
  lock: 'border border-line-strong text-ink-muted',
  database: 'border border-dashed border-ink bg-ink/10 text-ink',
  failed: 'border-2 border-danger text-danger',
}

const kindNames: Record<AttemptKind, string> = {
  won: 'got the seat',
  lock: 'turned away at the lock',
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
}

// The race simulation: many racers, one seat, one moment. What comes back is the API's own report.
export function RacePanel({ signedIn, seats, race, running, error, now, onStart }: RacePanelProps) {
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
          Racers ask for the first free seat at the same moment, through the same code as your own hold. One may get it.
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
        <p className="text-sm text-ink-muted">Enter as a guest to start a race.</p>
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
  const figures: { kind: AttemptKind; label: string }[] = [
    { kind: 'won', label: 'Got the seat' },
    { kind: 'lock', label: 'Stopped at the lock' },
    { kind: 'database', label: 'Refused by the database' },
  ]
  if (counts.failed > 0) figures.push({ kind: 'failed', label: 'Failed' })

  return (
    <div className="flex flex-col gap-4" aria-live="polite">
      <p className="text-base">
        <span className="font-semibold">{label}</span>: {race.winners} of {race.racers} got the seat, in{' '}
        <span className="font-mono tabular-nums">{race.durationMs}</span> ms.
      </p>

      {race.winners > 1 && (
        <p role="alert" className="border-l-2 border-danger pl-3 text-sm text-danger">
          More than one racer got the seat. That must never happen.
        </p>
      )}

      <ul className="grid max-w-[27rem] grid-cols-10 gap-1.5" aria-label="Racers">
        {race.attempts.map((attempt) => {
          const kind = attemptKind(attempt)
          return (
            <li
              key={attempt.racer}
              aria-label={`Racer ${attempt.racer}, ${kindNames[kind]}`}
              className={`flex aspect-square items-center justify-center rounded-seat font-mono text-xs tabular-nums ${racerStyles[kind]}`}
            >
              {attempt.racer}
            </li>
          )
        })}
      </ul>

      <dl className="flex flex-wrap gap-x-8 gap-y-3 border-y border-line py-3">
        {figures.map((figure) => (
          <div key={figure.kind} className="flex flex-col gap-1">
            <dt className="flex items-center gap-2 text-sm text-ink-muted">
              <span aria-hidden className={`size-3 shrink-0 rounded-seat ${racerStyles[figure.kind]}`} />
              {figure.label}
            </dt>
            <dd className="font-mono text-xl font-medium tabular-nums">{counts[figure.kind]}</dd>
          </div>
        ))}
      </dl>

      <Winner race={race} label={label} now={now} />

      <details className="text-sm">
        <summary className="w-fit cursor-pointer rounded-control font-medium underline decoration-line-strong underline-offset-4 hover:decoration-ink">
          Every attempt
        </summary>
        <div className="mt-3 max-h-72 overflow-auto" tabIndex={0} role="region" aria-label="Every attempt of the race">
          <table className="w-full min-w-[26rem] border-collapse font-mono text-xs tabular-nums">
            <thead className="sticky top-0 bg-bg text-left font-sans text-ink-muted">
              <tr>
                <th scope="col" className="py-1.5 pr-4 font-medium">Racer</th>
                <th scope="col" className="py-1.5 pr-4 font-medium">Result</th>
                <th scope="col" className="py-1.5 pr-4 font-medium">Lock</th>
                <th scope="col" className="py-1.5 pr-4 font-medium">Code</th>
                <th scope="col" className="py-1.5 pr-4 text-right font-medium">From</th>
                <th scope="col" className="py-1.5 text-right font-medium">To</th>
              </tr>
            </thead>
            <tbody>
              {race.attempts.map((attempt) => (
                <tr key={attempt.racer} className={attempt.outcome === 'won' ? 'font-semibold text-hold-text' : ''}>
                  <td className="py-1 pr-4">{attempt.racer}</td>
                  <td className="py-1 pr-4">{attempt.outcome}</td>
                  <td className="py-1 pr-4">{attempt.lock ?? 'none'}</td>
                  <td className="py-1 pr-4">{attempt.code ?? ''}</td>
                  <td className="py-1 pr-4 text-right">{attempt.startedAtMs} ms</td>
                  <td className="py-1 text-right">{attempt.finishedAtMs} ms</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </div>
  )
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
