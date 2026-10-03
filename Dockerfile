# Local build only. No registry publication or firmware tooling.
FROM node:22-bookworm-slim AS frontend
WORKDIR /build/src/ui
RUN npm install --global pnpm@10
COPY src/ui/package.json src/ui/pnpm-lock.yaml src/ui/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/ui/ ./
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend
WORKDIR /build
COPY src/PolterSense.csproj src/
RUN dotnet restore src/PolterSense.csproj
COPY src/ src/
COPY fixtures/ fixtures/
COPY --from=frontend /build/src/ui/build/ src/ui/build/
RUN dotnet publish src/PolterSense.csproj --configuration Release --no-restore \
    -p:SkipFrontendBuild=true --output /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5279 \
    POLTERSENSE_CONFIG_DIR=/config/poltersense
EXPOSE 5279
COPY --from=backend /out/ ./
RUN mkdir -p /config/poltersense && chown app:app /config/poltersense
USER app
VOLUME ["/config/poltersense"]
ENTRYPOINT ["dotnet", "PolterSense.dll"]
