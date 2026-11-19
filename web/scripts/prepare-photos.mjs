// One-off: turns the owner's Unsplash originals (repo-root /assets, 3–14 MB each, never committed) into web-sized JPEGs in
// public/photos. next/image then serves AVIF/WebP at the sizes each page needs.
//   node scripts/prepare-photos.mjs
import { mkdir, stat } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

const web = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const source = resolve(web, "..", "assets");
const target = join(web, "public", "photos");
const MAX_BYTES = 400 * 1024;

const PHOTOS = [
  ["1.jpg", "venice"],
  ["2.jpg", "main-street"],
  ["3.jpg", "cinque-terre"],
  ["4.jpg", "bavaria"],
  ["5.avif", "bukchon"],
];

await mkdir(target, { recursive: true });
for (const [file, name] of PHOTOS) {
  const out = join(target, `${name}.jpg`);
  let quality = 72;
  for (;;) {
    await sharp(join(source, file)).rotate().resize({ width: 1920, height: 1920, fit: "inside", withoutEnlargement: true }).jpeg({ quality, mozjpeg: true }).toFile(out);
    const { size } = await stat(out);
    if (size <= MAX_BYTES) {
      console.log(`${name}.jpg ${Math.round(size / 1024)} KB (q${quality})`);
      break;
    }

    if (quality <= 40) throw new Error(`${name}.jpg is still ${Math.round(size / 1024)} KB at quality ${quality}`);
    quality -= 8;
  }
}
