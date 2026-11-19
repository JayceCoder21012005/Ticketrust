import { cacheLife } from "next/cache";

/** Footer year. Cached so reading the clock doesn't make every page dynamic. */
export async function currentYear() {
  "use cache";
  cacheLife("days");
  return new Date().getFullYear();
}

/** The hour the home's daily hero photo is picked from. Cached (refreshed hourly) so it never makes a page dynamic. */
export async function currentHour(): Promise<string> {
  "use cache";
  cacheLife("hours");
  return new Date().toISOString();
}
