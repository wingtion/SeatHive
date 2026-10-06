import { useId, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react'
import type { Booking } from '../api/bookings'
import type { Seat } from '../api/events'
import { groupByRow, seatView, type SeatRow, type SeatView } from '../board/boardState'
import {
  HALL_WIDTH,
  STAGE_HEIGHT,
  STAGE_LABEL_Y,
  hallHeight,
  placeRowName,
  placeSeat,
  placeSectionName,
  stagePaths,
  type Place,
} from '../board/hall'
import { SEAT_NUMBER_TOP, SeatGlyph } from './SeatGlyph'

const viewNames: Record<SeatView, string> = {
  available: 'available',
  heldByOther: 'held by someone else',
  booked: 'booked',
  mineHeld: 'held by you',
  minePaying: 'held by you, payment in progress',
  mineBooked: 'booked by you',
}

// A seat is this wide in a hall 432 wide, and as high as its drawing makes it.
const SEAT_WIDTH = 28

interface SeatGridProps {
  seats: Seat[]
  mine: Map<number, Booking>
  busySeats: ReadonlySet<number>
  // False when nobody is signed in: the map can be read but no seat can be held.
  canHold: boolean
  onHold: (seatId: number) => void
  // A free seat was chosen by someone who is not signed in.
  onNeedsSignIn: () => void
}

// Where something stands in the hall, as a share of the hall's own size, so the map scales with its width.
function at(place: Place, height: number): CSSProperties {
  return { left: `${(place.x / HALL_WIDTH) * 100}%`, top: `${(place.y / height) * 100}%` }
}

// The hall: the stage on top, the rows in arcs around it, an aisle between the blocks of a row. Drawn from the
// section, row and number of each seat; the bend of the rows and the shape of the stage are not data.
export function SeatGrid({ seats, mine, busySeats, canHold, onHold, onNeedsSignIn }: SeatGridProps) {
  const hintId = useId()
  const hall = useRef<HTMLDivElement>(null)
  const rows = groupByRow(seats)
  const order = rows.map((row) => row.blocks.flat())
  const height = hallHeight(rows.length)

  // The map is one tab stop: the arrow keys move between its seats, and the seat last visited is the way back in.
  const [visited, setVisited] = useState<number | null>(null)
  const entry = seats.some((seat) => seat.seatId === visited) ? visited : (order[0]?.[0]?.seatId ?? null)

  function move(event: KeyboardEvent<HTMLDivElement>) {
    const seatId = Number((event.target as HTMLElement).dataset.seat)
    const rowIndex = order.findIndex((row) => row.some((seat) => seat.seatId === seatId))
    if (rowIndex < 0) return

    const row = order[rowIndex]
    const index = row.findIndex((seat) => seat.seatId === seatId)
    const inRow = (target: Seat[] | undefined) => target?.[Math.min(index, target.length - 1)]

    const next = {
      ArrowLeft: row[index - 1],
      ArrowRight: row[index + 1],
      ArrowUp: inRow(order[rowIndex - 1]),
      ArrowDown: inRow(order[rowIndex + 1]),
      Home: row[0],
      End: row[row.length - 1],
    }[event.key]
    if (!next) return

    event.preventDefault()
    hall.current?.querySelector<HTMLElement>(`[data-seat="${next.seatId}"]`)?.focus()
  }

  return (
    <div className="max-w-[27rem]">
      <p id={hintId} className="sr-only">
        Rows A to J, from the stage to the back. Use the arrow keys to move between seats.
      </p>
      <div
        ref={hall}
        role="group"
        aria-label="Seats"
        aria-describedby={hintId}
        onKeyDown={move}
        className="relative font-mono text-[9px] leading-none sm:text-[10px]"
        style={{ aspectRatio: `${HALL_WIDTH} / ${height}` }}
      >
        <Stage height={height} />
        {rows[0] && <SectionNames row={rows[0]} rowCount={rows.length} height={height} />}
        {rows.map((row, rowIndex) => (
          <Row
            key={row.name}
            row={row}
            rowIndex={rowIndex}
            rowCount={rows.length}
            height={height}
            entry={entry}
            mine={mine}
            busySeats={busySeats}
            canHold={canHold}
            onHold={onHold}
            onNeedsSignIn={onNeedsSignIn}
            onVisit={setVisited}
          />
        ))}
      </div>
    </div>
  )
}

// A platform with a wall behind it and a front edge bowed towards the seats. A filled shape and two lines, not a
// box with an outline, so it is not taken for a field to type in.
function Stage({ height }: { height: number }) {
  const paths = stagePaths()

  return (
    <>
      <svg viewBox={`0 0 ${HALL_WIDTH} ${STAGE_HEIGHT}`} aria-hidden className="absolute top-0 left-0 block w-full overflow-visible">
        <path d={paths.platform} style={{ fill: 'var(--stage)' }} />
        <path d={paths.apron} fill="none" strokeWidth="1.5" style={{ stroke: 'var(--ink)' }} />
        <path d={paths.wall} fill="none" strokeWidth="3" style={{ stroke: 'var(--ink)' }} />
      </svg>
      <p
        className="absolute left-1/2 -translate-x-1/2 -translate-y-1/2 pl-[0.28em] font-medium tracking-[0.28em] whitespace-nowrap text-ink-muted uppercase"
        style={{ top: `${(STAGE_LABEL_Y / height) * 100}%` }}
      >
        Stage
      </p>
    </>
  )
}

// The name of each section in front of its block. The rows and seats say where they are in their own names, so
// this line is for the eye only.
function SectionNames({ row, rowCount, height }: { row: SeatRow; rowCount: number; height: number }) {
  const blockSizes = row.blocks.map((block) => block.length)

  return (
    <>
      {row.blocks.map((block, blockIndex) => (
        <span
          key={block[0].section}
          aria-hidden
          className="absolute -translate-x-1/2 -translate-y-1/2 font-sans font-medium tracking-[0.1em] whitespace-nowrap text-ink-muted uppercase"
          style={at(placeSectionName(rowCount, blockSizes, blockIndex), height)}
        >
          {block[0].section}
        </span>
      ))}
    </>
  )
}

interface RowProps extends Omit<SeatGridProps, 'seats'> {
  row: SeatRow
  rowIndex: number
  rowCount: number
  // The height of the hall, in the unit its places are given in.
  height: number
  // The seat that takes the focus when the map is entered with Tab.
  entry: number | null
  onVisit: (seatId: number) => void
}

function Row({ row, rowIndex, rowCount, height, entry, mine, busySeats, canHold, onHold, onNeedsSignIn, onVisit }: RowProps) {
  const blockSizes = row.blocks.map((block) => block.length)
  // Where each block starts in the row, counted in seats.
  const starts = row.blocks.map((_, index) => row.blocks.slice(0, index).reduce((sum, block) => sum + block.length, 0))

  return (
    // The row is a group for its name only; it takes no room of its own, the seats stand in the hall.
    <div role="group" aria-label={`Row ${row.name}`} className="contents">
      <RowName name={row.name} style={at(placeRowName(rowIndex, rowCount, blockSizes, -1), height)} />
      {row.blocks.flatMap((block, blockIndex) =>
        block.map((seat, seatIndex) => {
          const view = seatView(seat, mine.get(seat.seatId))
          const free = view === 'available'
          const busy = busySeats.has(seat.seatId)
          const place = placeSeat(rowIndex, rowCount, blockSizes, blockIndex, starts[blockIndex] + seatIndex)

          return (
            <button
              key={seat.seatId}
              type="button"
              data-seat={seat.seatId}
              data-view={view}
              tabIndex={seat.seatId === entry ? 0 : -1}
              onFocus={() => onVisit(seat.seatId)}
              // Not the disabled attribute: a seat that cannot be held still says what it is to a screen reader.
              aria-disabled={!free || busy}
              aria-label={`Seat ${seat.seatNumber}, ${viewNames[view]}`}
              onClick={() => {
                if (!free || busy) return
                // Without a sign-in a free seat still answers: it says what is needed to hold it.
                if (canHold) onHold(seat.seatId)
                else onNeedsSignIn()
              }}
              style={{
                ...at(place, height),
                width: `${(SEAT_WIDTH / HALL_WIDTH) * 100}%`,
                // Turned to face the stage.
                transform: `translate(-50%, -50%) rotate(${place.turn.toFixed(2)}deg)`,
              }}
              className={
                'seat absolute aspect-[32/30] rounded-[3px] outline-offset-1 transition-opacity duration-150 ' +
                (free ? 'cursor-pointer' : 'cursor-default') +
                (busy ? ' opacity-60' : '')
              }
            >
              <SeatGlyph view={view} />
              {/* Turned back, so the number stands upright whichever way its seat faces. */}
              <span
                className="seat-num pointer-events-none absolute left-1/2 tabular-nums"
                style={{ top: SEAT_NUMBER_TOP, transform: `translate(-50%, -50%) rotate(${(-place.turn).toFixed(2)}deg)` }}
              >
                {seat.seatNumber}
              </span>
            </button>
          )
        }),
      )}
      <RowName name={row.name} style={at(placeRowName(rowIndex, rowCount, blockSizes, 1), height)} />
    </div>
  )
}

function RowName({ name, style }: { name: string; style: CSSProperties }) {
  return (
    <span aria-hidden className="absolute -translate-x-1/2 -translate-y-1/2 font-medium text-ink-muted" style={style}>
      {name}
    </span>
  )
}
