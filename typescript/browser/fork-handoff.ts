// The viewer hands a machine to a new terminal tab through localStorage (`topwater-fork:KEY`), and the new tab moves it
// to its own sessionStorage. A copy no tab moved (the tab never opened, or could not keep it) would stay for good, so
// the viewer drops those older than a day when it loads.
export const FORK_PREFIX = 'topwater-fork:';
const MAX_AGE_MS = 24 * 60 * 60 * 1000;

/** A new hand-off key: random, then the time in base 36 (8 digits until 2059), which dropStaleForks reads back. */
export const forkKey = (now = Date.now()) => Math.random().toString(36).slice(2, 12) + now.toString(36);

/** Removes hand-off copies older than a day (or whose key has no time); how many it removed. */
export function dropStaleForks(storage: Storage, now = Date.now()): number {
  const stale: string[] = [];
  for (let i = 0; i < storage.length; i++) {
    const k = storage.key(i);
    if (k?.startsWith(FORK_PREFIX) && !(Math.abs(now - parseInt(k.slice(-8), 36)) < MAX_AGE_MS)) stale.push(k);
  }
  for (const k of stale) storage.removeItem(k);
  return stale.length;
}
