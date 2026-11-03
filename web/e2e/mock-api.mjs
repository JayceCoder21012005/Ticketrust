// Stand-in for the .NET api in e2e and Lighthouse runs (no database in the web CI job). Serves the public
// catalog of spec 04 with fixture events whose sale dates are relative to start-up, so every CTA state exists.
// Usage: `node e2e/mock-api.mjs [port]` (default 3199). Imported by serve.mjs as well.
import { createServer } from "node:http";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const MIN = 60_000;
const DAY = 86_400_000;
const START = Date.now();
const at = (ms) => new Date(START + ms).toISOString();
/** 19:30 Vietnam time, `days` from now. */
const evening = (days) => {
  const d = new Date(START + days * DAY + 7 * 3_600_000);
  return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate(), 12, 30)).toISOString();
};

const VENUES = {
  myDinh: { id: "v-my-dinh", name: "Sân vận động Mỹ Đình", address: "Lê Đức Thọ, Nam Từ Liêm", city: "Hà Nội", mapUrl: "https://maps.google.com/?q=My+Dinh" },
  phuTho: { id: "v-phu-tho", name: "Nhà thi đấu Phú Thọ", address: "1 Lữ Gia, Quận 11", city: "Hồ Chí Minh", mapUrl: "https://maps.google.com/?q=Phu+Tho" },
  tienSon: { id: "v-tien-son", name: "Cung thể thao Tiên Sơn", address: "Phan Đăng Lưu, Hải Châu", city: "Đà Nẵng", mapUrl: null },
};

let seq = 0;
const id = (prefix) => `${prefix}-${(++seq).toString().padStart(4, "0")}-7000-8000-000000000000`;

function event({ slug, title, category, venue, days, tiers, phases, lineup = [], status = "Published", description }) {
  const startsAt = evening(days);
  return {
    id: id("e0000000"),
    slug,
    title,
    description: description ?? `${title}: một đêm diễn không thể bỏ lỡ.\n\nVé đã gồm phí, không phát sinh thêm khi thanh toán.`,
    category,
    lineup,
    coverImageUrl: null,
    startsAt,
    endsAt: new Date(Date.parse(startsAt) + 3 * 3_600_000).toISOString(),
    status,
    isSoldOut: false,
    venue,
    tiers: tiers.map(([name, price, kind = "GeneralAdmission", color = "#16a34a"]) => ({ id: id("t0000000"), name, price, currency: "VND", kind, color })),
    phases: phases.map(([name, opensIn, closesIn, queueEnabled, max = 4, code = false]) => ({
      id: id("p0000000"),
      name,
      opensAt: at(opensIn),
      closesAt: at(closesIn),
      queueEnabled,
      maxTicketsPerUser: max,
      requiresAccessCode: code,
    })),
  };
}

export const EVENTS = [
  event({
    slug: "son-tung-mtp-live", title: "Sơn Tùng M-TP Live", category: "concert", venue: VENUES.myDinh, days: 30, lineup: ["Sơn Tùng M-TP"],
    tiers: [["VIP", 3_000_000, "Seated", "#e11d48"], ["Standard", 1_500_000, "Seated", "#2563eb"], ["GA", 800_000]],
    phases: [["Mở bán", -DAY, 29 * DAY, true]],
  }),
  event({
    slug: "vu-cat-tuong-inner-space", title: "Vũ Cát Tường - Inner Space", category: "concert", venue: VENUES.myDinh, days: 35, lineup: ["Vũ Cát Tường"],
    tiers: [["GA", 900_000], ["Fanzone", 1_600_000]],
    phases: [["Mở bán", 20 * MIN, 34 * DAY, true]],
  }),
  event({
    slug: "ha-anh-tuan-chan-troi-ruc-ro", title: "Hà Anh Tuấn - Chân Trời Rực Rỡ", category: "concert", venue: VENUES.phuTho, days: 45, lineup: ["Hà Anh Tuấn", "Phương Linh"],
    tiers: [["VIP", 2_500_000, "Seated", "#e11d48"], ["GA", 600_000]],
    phases: [["Mở bán", 2 * DAY, 44 * DAY, true]],
  }),
  event({
    slug: "den-vau-show-2026", title: "Đen Vâu - Show Của Đen", category: "concert", venue: VENUES.phuTho, days: 60, lineup: ["Đen Vâu", "Hậu Vi"],
    tiers: [["GA", 700_000], ["VIP", 2_000_000]],
    phases: [["Presale", 14 * DAY, 15 * DAY, true, 2, true], ["Mở bán", 16 * DAY, 59 * DAY, true]],
  }),
  event({
    slug: "my-tam-tri-ky", title: "Mỹ Tâm - Tri Kỷ", category: "concert", venue: VENUES.tienSon, days: 25, lineup: ["Mỹ Tâm"],
    tiers: [["GA", 800_000], ["VIP", 2_200_000]], phases: [["Mở bán", -3 * DAY, 24 * DAY, true]],
  }),
  event({
    slug: "hozo-music-festival-2026", title: "HOZO Music Festival 2026", category: "festival", venue: VENUES.phuTho, days: 40, lineup: ["Ngọt", "Chillies"],
    tiers: [["1 ngày", 500_000], ["2 ngày", 850_000]], phases: [["Mở bán", 2 * DAY + 3 * 3_600_000, 39 * DAY, false, 6]],
  }),
  event({
    slug: "kich-idecaf-ngoi-nha-hanh-phuc", title: "Kịch IDECAF - Ngôi Nhà Hạnh Phúc", category: "theater", venue: VENUES.phuTho, days: 12,
    tiers: [["Ghế thường", 350_000], ["Ghế đẹp", 550_000]], phases: [["Mở bán", -5 * DAY, 11 * DAY, false, 8]],
  }),
  event({
    slug: "ballet-ho-thien-nga", title: "Ballet Hồ Thiên Nga", category: "theater", venue: VENUES.myDinh, days: 28,
    tiers: [["Hạng B", 600_000], ["Hạng A", 1_200_000]], phases: [["Mở bán", 6 * 3_600_000, 27 * DAY, false, 6]],
  }),
  event({
    slug: "v-league-ha-noi-vs-cong-an", title: "V.League: Hà Nội FC vs Công An Hà Nội", category: "sports", venue: VENUES.myDinh, days: 9,
    tiers: [["Khán đài B", 150_000], ["Khán đài A", 300_000]], phases: [["Mở bán", -2 * DAY, 8 * DAY, false, 8]],
  }),
  event({
    slug: "marathon-da-nang-2026", title: "Marathon Quốc tế Đà Nẵng 2026", category: "sports", venue: VENUES.tienSon, days: 50,
    tiers: [["10 km", 450_000], ["42 km", 850_000]], phases: [["Mở bán", 20 * DAY, 49 * DAY, false, 2]],
  }),
  event({
    slug: "saigon-teu-cuoi-xuyen-dem", title: "Saigon Tếu - Cười Xuyên Đêm", category: "comedy", venue: VENUES.phuTho, days: 18,
    tiers: [["Phổ thông", 250_000]], phases: [["Mở bán", -4 * DAY, 17 * DAY, false, 6]],
  }),
  event({
    slug: "workshop-thiet-ke-trai-nghiem", title: "Workshop Thiết kế Trải nghiệm Sự kiện", category: "workshop", venue: VENUES.tienSon, days: 7,
    tiers: [["Vé tham dự", 400_000]], phases: [["Mở bán", -10 * DAY, 6 * DAY, false, 2]],
  }),
  event({
    slug: "acoustic-dem-thu-ha-noi", title: "Acoustic Đêm Thu Hà Nội", category: "concert", venue: VENUES.myDinh, days: 70,
    tiers: [["GA", 450_000]], phases: [["Mở bán", 30 * DAY, 69 * DAY, false]],
  }),
  event({
    slug: "jazz-night-saigon", title: "Jazz Night Saigon", category: "concert", venue: VENUES.phuTho, days: -7, status: "Ended",
    tiers: [["GA", 500_000]], phases: [["Mở bán", -30 * DAY, -8 * DAY, false]],
  }),
  event({
    slug: "monsoon-festival-2026", title: "Monsoon Festival 2026", category: "festival", venue: VENUES.myDinh, days: 20, status: "Cancelled",
    tiers: [["GA", 600_000]], phases: [["Mở bán", 2 * DAY, 19 * DAY, true]],
  }),
];

const CATEGORIES = [
  { slug: "concert", name: "Nhạc sống" },
  { slug: "festival", name: "Lễ hội" },
  { slug: "theater", name: "Sân khấu" },
  { slug: "sports", name: "Thể thao" },
  { slug: "comedy", name: "Hài kịch" },
  { slug: "workshop", name: "Hội thảo" },
];

const fold = (s) => s.normalize("NFD").replace(/\p{M}/gu, "").replace(/đ/gi, "d").toLowerCase();

function summary(e) {
  const now = Date.now();
  const future = e.phases.filter((p) => Date.parse(p.opensAt) > now).map((p) => p.opensAt).sort();
  return {
    id: e.id,
    slug: e.slug,
    title: e.title,
    category: e.category,
    startsAt: e.startsAt,
    venueName: e.venue.name,
    city: e.venue.city,
    coverImageUrl: e.coverImageUrl,
    priceFrom: e.tiers.length ? Math.min(...e.tiers.map((t) => t.price)) : null,
    nextSaleOpensAt: future[0] ?? null,
    isOnSale: e.phases.some((p) => Date.parse(p.opensAt) <= now && now < Date.parse(p.closesAt)),
    isSoldOut: e.isSoldOut,
  };
}

function list(params) {
  const q = params.get("q");
  const terms = q ? fold(q).split(/\s+/).filter(Boolean) : [];
  const from = params.get("from") ? Date.parse(params.get("from")) : -Infinity;
  const to = params.get("to") ? Date.parse(params.get("to")) : Infinity;
  const priceMax = params.get("priceMax") ? Number(params.get("priceMax")) : Infinity;
  const limit = Number(params.get("limit") ?? 20);
  const offset = Number(params.get("cursor") ?? 0);

  const matches = EVENTS.filter((e) => e.status === "Published" && Date.parse(e.endsAt) > Date.now())
    .filter((e) => terms.every((term) => fold(`${e.title} ${e.lineup.join(" ")} ${e.venue.city}`).includes(term)))
    .filter((e) => !params.get("city") || e.venue.city === params.get("city"))
    .filter((e) => !params.get("category") || e.category === params.get("category"))
    .filter((e) => Date.parse(e.startsAt) >= from && Date.parse(e.startsAt) <= to)
    .filter((e) => e.tiers.some((t) => t.price <= priceMax))
    .sort((a, b) => Date.parse(a.startsAt) - Date.parse(b.startsAt));

  const items = matches.slice(offset, offset + limit).map(summary);
  return { items, nextCursor: offset + limit < matches.length ? String(offset + limit) : null };
}

function route(url) {
  const path = url.pathname.replace(/^\/api\/v1/, "");
  if (path === "/time") return [200, { serverNow: new Date().toISOString() }];
  if (path === "/categories") return [200, CATEGORIES];
  if (path === "/cities") return [200, ["Đà Nẵng", "Hà Nội", "Hồ Chí Minh"]];
  if (path === "/events" || path === "/events/") return [200, list(url.searchParams)];

  const availability = /^\/events\/([^/]+)\/availability$/.exec(path);
  if (availability) {
    const e = EVENTS.find((x) => x.id === availability[1] && (x.status === "Published" || x.status === "Ended"));
    return e
      ? [200, { serverNow: new Date().toISOString(), version: 0, unavailable: [], tiers: e.tiers.map((t) => ({ tierId: t.id, remaining: 100 })) }]
      : [404, { code: "event.not_found" }];
  }

  const detail = /^\/events\/([^/]+)$/.exec(path);
  if (detail) {
    const e = EVENTS.find((x) => x.slug === detail[1] && (x.status === "Published" || x.status === "Ended"));
    return e ? [200, e] : [404, { code: "event.not_found", status: 404, title: "Event not found." }];
  }

  return [404, { code: "not_found" }];
}

export function startMockApi(port = 3199) {
  const server = createServer((request, response) => {
    const [status, body] = route(new URL(request.url ?? "/", `http://localhost:${port}`));
    response.writeHead(status, { "content-type": status >= 400 ? "application/problem+json" : "application/json" });
    response.end(JSON.stringify(body));
  });

  return new Promise((resolve) => server.listen(port, () => resolve(server)));
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const port = Number(process.argv[2] ?? 3199);
  await startMockApi(port);
  console.log(`Mock api ready on http://localhost:${port}`);
}
