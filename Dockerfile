FROM node:24.22.2-bookworm-slim@sha256:c86ecead9e55a5210ac1727de38a4ca840bf03dcaf34dc9749addbf383b1920c AS shared-build
WORKDIR /home-ia-shared
COPY --from=shared_source /package.json /package-lock.json /tsconfig.json ./
COPY --from=shared_source /src ./src
RUN npm ci --omit=peer && npm run build

FROM node:24.22.2-bookworm-slim@sha256:c86ecead9e55a5210ac1727de38a4ca840bf03dcaf34dc9749addbf383b1920c AS web-build
WORKDIR /web
COPY --from=shared-build /home-ia-shared/package.json /home-ia-shared/
COPY --from=shared-build /home-ia-shared/dist /home-ia-shared/dist
COPY --from=shared-build /home-ia-shared/src /home-ia-shared/src
COPY --from=web_source /package.json /package-lock.json ./
RUN npm ci
COPY --from=web_source /index.html ./index.html
COPY --from=web_source /vite.config.ts ./vite.config.ts
COPY --from=web_source /tsconfig.json /tsconfig.app.json /tsconfig.node.json ./
COPY --from=web_source /src ./src
COPY --from=web_source /public ./public
ENV VITE_MONITORING_REMOTE_URL=/monitoring/remoteEntry.js
ENV VITE_PLATFORM_REMOTE_URL=/platform/remoteEntry.js
ENV VITE_ACTIVITYWATCH_REMOTE_URL=/activitywatch/remoteEntry.js
RUN npm run build

FROM node:24.22.2-bookworm-slim@sha256:c86ecead9e55a5210ac1727de38a4ca840bf03dcaf34dc9749addbf383b1920c AS monitoring-web-build
WORKDIR /monitoring-web
COPY --from=shared-build /home-ia-shared/package.json /home-ia-shared/
COPY --from=shared-build /home-ia-shared/dist /home-ia-shared/dist
COPY --from=shared-build /home-ia-shared/src /home-ia-shared/src
COPY --from=monitoring_web_source /package.json /package-lock.json ./
RUN npm ci
COPY --from=monitoring_web_source /index.html ./index.html
COPY --from=monitoring_web_source /vite.config.ts ./vite.config.ts
COPY --from=monitoring_web_source /tsconfig.json /tsconfig.app.json /tsconfig.node.json ./
COPY --from=monitoring_web_source /src ./src
COPY --from=monitoring_web_source /public ./public
RUN npm run build

FROM node:24.22.2-bookworm-slim@sha256:c86ecead9e55a5210ac1727de38a4ca840bf03dcaf34dc9749addbf383b1920c AS platform-web-build
WORKDIR /platform-web
COPY --from=shared-build /home-ia-shared/package.json /home-ia-shared/
COPY --from=shared-build /home-ia-shared/dist /home-ia-shared/dist
COPY --from=shared-build /home-ia-shared/src /home-ia-shared/src
COPY --from=platform_web_source /package.json /package-lock.json ./
RUN npm ci
COPY --from=platform_web_source /index.html ./index.html
COPY --from=platform_web_source /vite.config.ts ./vite.config.ts
COPY --from=platform_web_source /tsconfig.json /tsconfig.app.json /tsconfig.node.json ./
COPY --from=platform_web_source /src ./src
RUN npm run build

FROM node:24.22.2-bookworm-slim@sha256:c86ecead9e55a5210ac1727de38a4ca840bf03dcaf34dc9749addbf383b1920c AS activitywatch-web-build
WORKDIR /activitywatch-web
COPY --from=shared-build /home-ia-shared/package.json /home-ia-shared/
COPY --from=shared-build /home-ia-shared/dist /home-ia-shared/dist
COPY --from=shared-build /home-ia-shared/src /home-ia-shared/src
COPY --from=activitywatch_web_source /package.json /package-lock.json ./
RUN npm ci
COPY --from=activitywatch_web_source /index.html ./index.html
COPY --from=activitywatch_web_source /vite.config.ts ./vite.config.ts
COPY --from=activitywatch_web_source /tsconfig.json /tsconfig.app.json /tsconfig.node.json ./
COPY --from=activitywatch_web_source /src ./src
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b

ARG APP_UID=1000
ARG APP_GID=1000
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg=7:6.1.1-3ubuntu5 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /workspace
COPY --from=web-build /web/dist/ /app/web/
COPY --from=monitoring-web-build /monitoring-web/dist/ /app/web/monitoring/
COPY --from=platform-web-build /platform-web/dist/ /app/web/platform/
COPY --from=activitywatch-web-build /activitywatch-web/dist/ /app/web/activitywatch/
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_HOME=/tmp \
    NUGET_PACKAGES=/tmp/nuget \
    HOME=/tmp
USER ${APP_UID}:${APP_GID}
