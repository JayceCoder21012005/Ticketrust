/** The eight ISO 18004 data masks and the four-rule penalty used to choose one (section 7.8). */
export type Grid = boolean[][];

const MASKS: ((row: number, col: number) => boolean)[] = [
  (r, c) => (r + c) % 2 === 0,
  (r) => r % 2 === 0,
  (_, c) => c % 3 === 0,
  (r, c) => (r + c) % 3 === 0,
  (r, c) => (Math.floor(r / 2) + Math.floor(c / 3)) % 2 === 0,
  (r, c) => ((r * c) % 2) + ((r * c) % 3) === 0,
  (r, c) => (((r * c) % 2) + ((r * c) % 3)) % 2 === 0,
  (r, c) => (((r + c) % 2) + ((r * c) % 3)) % 2 === 0,
];

/** A copy of `grid` with mask `index` applied to every module that is not a function pattern. */
export function applyMask(grid: Grid, isFunction: Grid, index: number): Grid {
  const mask = MASKS[index]!;
  return grid.map((row, r) => row.map((dark, c) => (isFunction[r]![c] ? dark : dark !== mask(r, c))));
}

const columns = (grid: Grid): Grid => grid.map((_, c) => grid.map((row) => row[c]!));

/** Rule 1: 3 + (n − 5) for every run of n ≥ 5 same-coloured modules in a line. */
function runPenalty(line: boolean[]): number {
  let penalty = 0;
  let run = 1;
  for (let i = 1; i <= line.length; i++) {
    if (i < line.length && line[i] === line[i - 1]) {
      run++;
      continue;
    }
    if (run >= 5) {
      penalty += run - 2;
    }
    run = 1;
  }

  return penalty;
}

/** Rule 2: 3 for every 2×2 block of one colour. */
function blockPenalty(grid: Grid): number {
  let penalty = 0;
  for (let r = 0; r < grid.length - 1; r++) {
    for (let c = 0; c < grid.length - 1; c++) {
      const dark = grid[r]![c];
      if (grid[r]![c + 1] === dark && grid[r + 1]![c] === dark && grid[r + 1]![c + 1] === dark) {
        penalty += 3;
      }
    }
  }

  return penalty;
}

const FINDER_LIKE = ["10111010000", "00001011101"];

/** Rule 3: 40 for every 1:1:3:1:1 finder-like pattern with four light modules on one side. */
function finderPenalty(line: boolean[]): number {
  const text = line.map((dark) => (dark ? "1" : "0")).join("");
  let penalty = 0;
  for (let i = 0; i + 11 <= text.length; i++) {
    if (FINDER_LIKE.includes(text.slice(i, i + 11))) {
      penalty += 40;
    }
  }

  return penalty;
}

/** Rule 4: 10 for every 5 % the dark share is away from 50 %. */
function balancePenalty(grid: Grid): number {
  const total = grid.length * grid.length;
  const dark = grid.reduce((sum, row) => sum + row.filter(Boolean).length, 0);
  return 10 * Math.floor(Math.abs(dark * 100 - total * 50) / (total * 5));
}

export function penalty(grid: Grid): number {
  const lines = [...grid, ...columns(grid)];
  const perLine = lines.reduce((sum, line) => sum + runPenalty(line) + finderPenalty(line), 0);
  return perLine + blockPenalty(grid) + balancePenalty(grid);
}

export const MASK_COUNT = MASKS.length;
