# 1. Build the Vue dashboard
FROM node:22-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package*.json ./
RUN npm ci || npm install
COPY frontend/ ./
RUN npm run build

# 2. Build the ASP.NET Core backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY backend/Schermpostvak.Api/Schermpostvak.Api.csproj backend/Schermpostvak.Api/
RUN dotnet restore backend/Schermpostvak.Api/Schermpostvak.Api.csproj
COPY backend/ backend/
RUN dotnet publish backend/Schermpostvak.Api/Schermpostvak.Api.csproj -c Release -o /app

# 3. Runtime with ffmpeg for video conversion and thumbnails
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=backend /app ./
COPY --from=frontend /src/frontend/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Schermpostvak.Api.dll"]
