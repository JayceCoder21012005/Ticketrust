import { cacheLife } from "next/cache";

/** Footer year. Cached so reading the clock doesn't make every page dynamic. */
export async function currentYear() {
  "use cache";
  cacheLife("days");
  return new Date().getFullYear();
}
