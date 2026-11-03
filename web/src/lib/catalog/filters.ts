/**
 * Listing filters live in the URL (spec 04: shareable links). Dates are whole days in Vietnam time; the
 * presets ("Hôm nay", "Cuối tuần này", "Tháng này") stay presets in the URL so a shared link stays relative.
 */
export const DATE_PRESETS = ["today", "weekend", "month"] as const;
export type DatePreset = (typeof DATE_PRESETS)[number];

export type EventFilters = {
  q?: string;
  category?: string;
  city?: string;
  when?: DatePreset;
  /** `YYYY-MM-DD`, Vietnam day. */
  from?: string;
  to?: string;
  /** Whole dong. */
  priceMax?: number;
};

const KEY_ORDER = ["q", "category", "city", "when", "from", "to", "priceMax"] as const;
const MAX_SEARCH_LENGTH = 100;
const VN_OFFSET_MS = 7 * 3_600_000;

type SearchParamsLike = URLSearchParams | Record<string, string | string[] | undefined>;

function read(params: SearchParamsLike, key: string): string | undefined {
  const value = params instanceof URLSearchParams ? params.get(key) : params[key];
  const first = Array.isArray(value) ? value[0] : value;
  const trimmed = first?.trim();
  return trimmed ? trimmed : undefined;
}

function isDay(value: string | undefined): value is string {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return false;
  }

  const date = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(date.getTime()) && date.toISOString().startsWith(value);
}

export function parseFilters(params: SearchParamsLike): EventFilters {
  const filters: EventFilters = {};

  const q = read(params, "q");
  if (q) filters.q = q.slice(0, MAX_SEARCH_LENGTH);

  const category = read(params, "category");
  if (category) filters.category = category;

  const city = read(params, "city");
  if (city) filters.city = city;

  const when = read(params, "when");
  if (when && (DATE_PRESETS as readonly string[]).includes(when)) filters.when = when as DatePreset;

  const from = read(params, "from");
  if (isDay(from)) filters.from = from;

  const to = read(params, "to");
  if (isDay(to)) filters.to = to;

  const priceMax = Number(read(params, "priceMax"));
  if (Number.isSafeInteger(priceMax) && priceMax > 0) filters.priceMax = priceMax;

  return filters;
}

export function filtersToSearchParams(filters: EventFilters): URLSearchParams {
  const params = new URLSearchParams();
  for (const key of KEY_ORDER) {
    const value = filters[key];
    if (value !== undefined && value !== "") {
      params.set(key, String(value));
    }
  }

  return params;
}

/** Link to the listing with these filters (used by server and client components alike). */
export function listingHref(filters: EventFilters): string {
  const search = filtersToSearchParams(filters).toString();
  return search ? `/events?${search}` : "/events";
}

/** Shown on the mobile "Bộ lọc (n)" button. The search box is visible on its own, so it doesn't count. */
export function activeFilterCount(filters: EventFilters): number {
  const { category, city, when, from, to, priceMax } = filters;
  return [category, city, when ?? from ?? to, priceMax].filter((value) => value !== undefined).length;
}

type VnDay = { year: number; month: number; day: number; weekday: number };

function vnDay(instant: Date, addDays = 0): VnDay {
  const shifted = new Date(instant.getTime() + VN_OFFSET_MS + addDays * 86_400_000);
  return {
    year: shifted.getUTCFullYear(),
    month: shifted.getUTCMonth() + 1,
    day: shifted.getUTCDate(),
    weekday: shifted.getUTCDay(),
  };
}

const pad = (n: number) => n.toString().padStart(2, "0");
const dayString = ({ year, month, day }: VnDay) => `${year}-${pad(month)}-${pad(day)}`;
const startOf = (day: string) => `${day}T00:00:00+07:00`;
const endOf = (day: string) => `${day}T23:59:59.999+07:00`;

function presetRange(preset: DatePreset, now: Date): [string, string] {
  const today = vnDay(now);

  switch (preset) {
    case "today":
      return [dayString(today), dayString(today)];
    case "weekend": {
      // Saturday and Sunday of this week; on Sunday only Sunday is left.
      const toSaturday = today.weekday === 0 ? 0 : 6 - today.weekday;
      const toSunday = today.weekday === 0 ? 0 : toSaturday + 1;
      return [dayString(vnDay(now, toSaturday)), dayString(vnDay(now, toSunday))];
    }
    case "month": {
      const lastDay = new Date(Date.UTC(today.year, today.month, 0)).getUTCDate();
      return [dayString(today), dayString({ ...today, day: lastDay })];
    }
  }
}

/** Query string for `GET /api/v1/events`. Presets are resolved against `now` (server time when known). */
export function toApiQuery(filters: EventFilters, now: Date): Record<string, string> {
  const query: Record<string, string> = {};
  if (filters.q) query.q = filters.q;
  if (filters.category) query.category = filters.category;
  if (filters.city) query.city = filters.city;
  if (filters.priceMax !== undefined) query.priceMax = String(filters.priceMax);

  const [from, to] = filters.when ? presetRange(filters.when, now) : [filters.from, filters.to];
  if (from) query.from = startOf(from);
  if (to) query.to = endOf(to);

  return query;
}
