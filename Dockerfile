# ---- web app: the React frontend from the salon-tracker repository (git submodule) ----
FROM node:24-alpine AS web
WORKDIR /src
COPY frontend/package.json frontend/package-lock.json ./
COPY frontend/web/package.json web/package.json
COPY frontend/server/package.json server/package.json
# only the web workspace: none of the Node server's dependencies are needed here
RUN npm ci -w web --include-workspace-root=false --no-audit --no-fund
COPY frontend/web web
RUN npm run build -w web

# ---- API ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS api
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SalonTracker.Api/SalonTracker.Api.csproj src/SalonTracker.Api/
RUN dotnet restore src/SalonTracker.Api/SalonTracker.Api.csproj
COPY src src
RUN dotnet publish src/SalonTracker.Api -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# pg_dump for the nightly backup (must match the postgres:17 server); tzdata for SALON_TZ
RUN apk add --no-cache postgresql17-client tzdata
WORKDIR /app
COPY --from=api /app .
COPY --from=web /src/web/dist wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 3001
# applies pending migrations, then starts the API (which also serves the web app);
# with an argument it runs a tool instead: seed, backup, reset-password, ...
ENTRYPOINT ["dotnet", "SalonTracker.Api.dll"]
