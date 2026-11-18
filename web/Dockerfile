# syntax=docker/dockerfile:1
# Build from web/: docker build --build-arg API_URL=http://api:8080 web
FROM node:24-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci

FROM node:24-alpine AS build
WORKDIR /app
# Rewrites are resolved at build time, so the api origin is baked into the image.
ARG API_URL=http://api:8080
# Turnstile site key (spec 12): public, inlined into the client bundle. Without it the web sends Cloudflare's test token,
# which a production api refuses.
ARG NEXT_PUBLIC_TURNSTILE_SITE_KEY=
ENV API_URL=$API_URL NEXT_TELEMETRY_DISABLED=1 NEXT_PUBLIC_TURNSTILE_SITE_KEY=$NEXT_PUBLIC_TURNSTILE_SITE_KEY
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN npm run build

FROM node:24-alpine AS final
WORKDIR /app
# Server components also call the api directly (catalog pages, spec 04), so the origin is needed at runtime too.
ARG API_URL=http://api:8080
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 PORT=3000 HOSTNAME=0.0.0.0 API_URL=$API_URL
COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
COPY --from=build --chown=node:node /app/public ./public
# Be Vietnam Pro for Open Graph images (read from disk at request time).
COPY --from=build --chown=node:node /app/assets ./assets
USER node
EXPOSE 3000
CMD ["node", "server.js"]
