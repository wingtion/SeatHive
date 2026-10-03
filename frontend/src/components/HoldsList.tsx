import { formatRemaining, seatLabel } from '../board/boardState'
import type { HoldNow } from '../hood/hoodState'

const holders: Record<HoldNow['holder'], string> = {
  you: 'you',
  race: 'the race winner',
  other: 'someone else',
}

// More holds than this are counted, not listed.
const SHOWN = 6

// Every seat that is held right now, counting down to the moment it is let go.
export function HoldsList({ holds, now }: { holds: HoldNow[]; now: number }) {
  const shown = holds.slice(0, SHOWN)

  return (
    <section aria-labelledby="holds-heading" className="flex flex-col gap-4">
      <div className="flex flex-col gap-1.5">
        <h3 id="holds-heading" className="text-lg font-semibold">
          Holds running out
        </h3>
        <p className="max-w-[65ch] text-sm text-ink-muted">
          A hold ends by itself: the API frees the seat within five seconds of the time shown.
        </p>
      </div>

      {holds.length === 0 ? (
        <p className="text-sm text-ink-muted">No seat is held right now. Hold one, or start a race.</p>
      ) : (
        <ul className="flex max-w-md flex-col gap-2">
          {shown.map((hold) => {
            const remaining = hold.until - now
            return (
              <li key={hold.seat.seatId} className="grid grid-cols-[4rem_1fr_auto] items-baseline gap-3 text-sm">
                <span className="font-mono font-medium">{seatLabel(hold.seat)}</span>
                <span className="text-ink-muted">held by {holders[hold.holder]}</span>
                {remaining > 0 ? (
                  <span className="font-mono font-medium tabular-nums text-hold-text">{formatRemaining(remaining)}</span>
                ) : (
                  <span className="text-ink-muted">being freed</span>
                )}
              </li>
            )
          })}
        </ul>
      )}

      {holds.length > SHOWN && <p className="text-sm text-ink-muted">And {holds.length - SHOWN} more.</p>}
    </section>
  )
}
