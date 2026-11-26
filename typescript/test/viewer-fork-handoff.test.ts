// The viewer's hand-off of a machine to a new tab (browser/fork-handoff.ts): copies no tab picked up are dropped after a day.
import { describe, expect, it } from 'vitest';
import { FORK_PREFIX, dropStaleForks, forkKey } from '../browser/fork-handoff.js';

class MemoryStorage implements Storage {
  private m = new Map<string, string>();
  get length() { return this.m.size; }
  key(i: number) { return [...this.m.keys()][i] ?? null; }
  getItem(k: string) { return this.m.get(k) ?? null; }
  setItem(k: string, v: string) { this.m.set(k, String(v)); }
  removeItem(k: string) { this.m.delete(k); }
  clear() { this.m.clear(); }
}

describe('fork hand-off', () => {
  it('drops hand-off copies older than a day and keeps everything else', () => {
    const now = Date.UTC(2026, 10, 26, 12), hour = 3600_000, s = new MemoryStorage();
    const fresh = FORK_PREFIX + forkKey(now - hour), old = FORK_PREFIX + forkKey(now - 25 * hour), justUnder = FORK_PREFIX + forkKey(now - 23 * hour);
    for (const k of [fresh, old, justUnder, FORK_PREFIX + 'nonsense!', 'theme']) s.setItem(k, 'x');
    expect(forkKey(now)).toMatch(/^[a-z0-9]{1,40}$/); // what the terminal page accepts
    expect(dropStaleForks(s, now)).toBe(2);
    expect([...Array(s.length).keys()].map(i => s.key(i)).sort()).toEqual([fresh, justUnder, 'theme'].sort());
    expect(dropStaleForks(s, now)).toBe(0);
  });
});
