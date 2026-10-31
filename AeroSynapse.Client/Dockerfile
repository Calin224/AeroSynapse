FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . ./
RUN npm run build

FROM nginx:1.28-alpine AS runtime
RUN apk add --no-cache jq
ENV API_UPSTREAM=host.docker.internal:5080 \
    INTELLIGENCE_UPSTREAM=host.docker.internal:8000 \
    FRONTEND_API_BASE_URL=/api \
    FRONTEND_INTELLIGENCE_BASE_URL=/intelligence
COPY --from=build /app/dist/AeroSynapse.Client/browser /usr/share/nginx/html
COPY docker/default.conf.template /etc/nginx/templates/default.conf.template
COPY docker/30-runtime-config.sh /docker-entrypoint.d/30-runtime-config.sh
RUN chmod +x /docker-entrypoint.d/30-runtime-config.sh
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1/health || exit 1
