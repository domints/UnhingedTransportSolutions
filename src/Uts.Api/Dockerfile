# ---- Vue build ----
FROM node:20-alpine AS web
WORKDIR /web
COPY src/Uts.Web/package.json src/Uts.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Uts.Web ./
RUN npm run build

# ---- .NET build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/Uts.Api/Uts.Api.csproj ./
RUN dotnet restore
COPY src/Uts.Api ./
RUN dotnet publish -c Release -o /app

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app .
COPY --from=web /web/dist ./wwwroot
ENTRYPOINT ["dotnet", "Uts.Api.dll"]
