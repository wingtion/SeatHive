// Where things stand in the hall as it is drawn. The unit is a pixel of a hall 432 wide; the map scales it.
// Only the section, row and number of a seat are data: the bend of the rows and their spacing are presentation.

export const HALL_WIDTH = 432

// The rows are arcs around one point behind the stage: row A has this radius, every row after it one pitch more.
const FIRST_RADIUS = 560
const ROW_PITCH = 34
const FIRST_ROW_Y = 112
const CENTRE_X = HALL_WIDTH / 2
const CENTRE_Y = FIRST_ROW_Y - FIRST_RADIUS

// Along a row: from one seat to the next, the extra room of an aisle, and from the last seat to the row's letter.
const SEAT_PITCH = 34
const AISLE = 14
const ROW_NAME_GAP = 28

// The front row is drawn a little closer together than the back row, so the hall opens like a fan.
const FRONT_SPREAD = 0.92

export interface Place {
  x: number
  y: number
  // Degrees to turn by, so that what stands here faces the stage.
  turn: number
}

function onArc(radius: number, along: number): Place {
  const angle = along / radius
  return { x: CENTRE_X + radius * Math.sin(angle), y: CENTRE_Y + radius * Math.cos(angle), turn: (-angle * 180) / Math.PI }
}

function spread(rowIndex: number, rowCount: number): number {
  return rowCount > 1 ? FRONT_SPREAD + ((1 - FRONT_SPREAD) * rowIndex) / (rowCount - 1) : 1
}

function seatCount(blockSizes: number[]): number {
  return blockSizes.reduce((sum, size) => sum + size, 0)
}

// How far along its row a seat stands from the middle, before the row is spread: the blocks move apart by an aisle.
function alongRow(blockSizes: number[], blockIndex: number, indexInRow: number): number {
  return (indexInRow - (seatCount(blockSizes) - 1) / 2) * SEAT_PITCH + (blockIndex - (blockSizes.length - 1) / 2) * AISLE
}

export function hallHeight(rowCount: number): number {
  return FIRST_ROW_Y + Math.max(0, rowCount - 1) * ROW_PITCH + 22
}

// The middle of a seat. blockSizes are the seats of each block of its row; indexInRow counts from the left end.
export function placeSeat(rowIndex: number, rowCount: number, blockSizes: number[], blockIndex: number, indexInRow: number): Place {
  return onArc(FIRST_RADIUS + rowIndex * ROW_PITCH, alongRow(blockSizes, blockIndex, indexInRow) * spread(rowIndex, rowCount))
}

// The row's letter, past the last seat at the left (-1) or the right (1) end.
export function placeRowName(rowIndex: number, rowCount: number, blockSizes: number[], side: -1 | 1): Place {
  const end = ((seatCount(blockSizes) - 1) / 2) * SEAT_PITCH + ((blockSizes.length - 1) / 2) * AISLE + ROW_NAME_GAP
  return onArc(FIRST_RADIUS + rowIndex * ROW_PITCH, side * end * spread(rowIndex, rowCount))
}

// The name of a section, one row's pitch in front of the middle of its block in the first row.
export function placeSectionName(rowCount: number, blockSizes: number[], blockIndex: number): Place {
  const before = blockSizes.slice(0, blockIndex).reduce((sum, size) => sum + size, 0)
  const middle = before + (blockSizes[blockIndex] - 1) / 2
  return onArc(FIRST_RADIUS - ROW_PITCH, alongRow(blockSizes, blockIndex, middle) * spread(0, rowCount))
}

// The stage: a platform narrower than the hall. Its front edge is an arc around the same point as the rows.
export const STAGE_HEIGHT = 64
export const STAGE_LABEL_Y = 31

const APRON_RADIUS = FIRST_RADIUS - 58
const APRON_HALF = 80
const WALL_HALF = 92
const WALL_Y = 10

export function stagePaths(): { platform: string; apron: string; wall: string } {
  const edgeY = (CENTRE_Y + APRON_RADIUS * Math.cos(Math.asin(APRON_HALF / APRON_RADIUS))).toFixed(2)
  const arc = `A${APRON_RADIUS} ${APRON_RADIUS} 0 0 1 ${CENTRE_X - APRON_HALF} ${edgeY}`
  return {
    platform: `M${CENTRE_X - WALL_HALF} ${WALL_Y}H${CENTRE_X + WALL_HALF}L${CENTRE_X + APRON_HALF} ${edgeY}${arc}Z`,
    apron: `M${CENTRE_X + APRON_HALF} ${edgeY}${arc}`,
    wall: `M${CENTRE_X - WALL_HALF - 8} ${WALL_Y}H${CENTRE_X + WALL_HALF + 8}`,
  }
}
