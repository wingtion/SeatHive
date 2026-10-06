import { describe, expect, it } from 'vitest'
import { HALL_WIDTH, hallHeight, placeRowName, placeSeat, placeSectionName } from './hall'

// The demo hall: ten rows of a left block of three, a centre block of four and a right block of three.
const blocks = [3, 4, 3]
const rows = 10

function blockOf(indexInRow: number): number {
  return indexInRow < 3 ? 0 : indexInRow < 7 ? 1 : 2
}

describe('placeSeat', () => {
  it('puts the two halves of a row at the same height, as far from the middle', () => {
    for (let index = 0; index < 5; index++) {
      const left = placeSeat(0, rows, blocks, blockOf(index), index)
      const right = placeSeat(0, rows, blocks, blockOf(9 - index), 9 - index)
      expect(left.y).toBeCloseTo(right.y)
      expect(left.x + right.x).toBeCloseTo(HALL_WIDTH)
      expect(left.turn).toBeCloseTo(-right.turn)
    }
  })

  it('bends a row around the stage: its ends are nearer the stage than its middle and turned towards it', () => {
    const end = placeSeat(0, rows, blocks, 0, 0)
    const middle = placeSeat(0, rows, blocks, 1, 4)
    expect(end.y).toBeLessThan(middle.y)
    expect(end.turn).toBeGreaterThan(0)
    expect(placeSeat(0, rows, blocks, 2, 9).turn).toBeLessThan(0)
  })

  it('leaves an aisle between two blocks', () => {
    const inBlock = placeSeat(0, rows, blocks, 0, 1).x - placeSeat(0, rows, blocks, 0, 0).x
    const acrossAisle = placeSeat(0, rows, blocks, 1, 3).x - placeSeat(0, rows, blocks, 0, 2).x
    expect(acrossAisle).toBeGreaterThan(inBlock + 10)
  })

  it('keeps every seat, row letter and section name inside the hall and clear of the stage', () => {
    // The front edge of the stage is lowest in the middle, at 54.
    const stageEdge = 54
    const height = hallHeight(rows)
    const places = [0, 1, 2].map((block) => placeSectionName(rows, blocks, block))
    for (let row = 0; row < rows; row++) {
      places.push(placeRowName(row, rows, blocks, -1), placeRowName(row, rows, blocks, 1))
      for (let index = 0; index < 10; index++) places.push(placeSeat(row, rows, blocks, blockOf(index), index))
    }

    for (const place of places) {
      expect(place.x).toBeGreaterThan(14)
      expect(place.x).toBeLessThan(HALL_WIDTH - 14)
      expect(place.y).toBeGreaterThan(stageEdge + 5)
      expect(place.y).toBeLessThan(height - 13)
    }
  })

  it('places a hall of one row of one seat in the middle', () => {
    const only = placeSeat(0, 1, [1], 0, 0)
    expect(only.x).toBeCloseTo(HALL_WIDTH / 2)
    expect(only.turn).toBeCloseTo(0)
  })
})
