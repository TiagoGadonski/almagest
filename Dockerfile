# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first so restore is its own cached layer, independent
# of source edits.
COPY Directory.Build.props .
COPY src/Almagest.Domain/Almagest.Domain.csproj src/Almagest.Domain/
COPY src/Almagest.Application/Almagest.Application.csproj src/Almagest.Application/
COPY src/Almagest.Infrastructure/Almagest.Infrastructure.csproj src/Almagest.Infrastructure/
COPY src/Almagest.Api/Almagest.Api.csproj src/Almagest.Api/
RUN dotnet restore src/Almagest.Api/Almagest.Api.csproj

COPY src/Almagest.Domain/ src/Almagest.Domain/
COPY src/Almagest.Application/ src/Almagest.Application/
COPY src/Almagest.Infrastructure/ src/Almagest.Infrastructure/
COPY src/Almagest.Api/ src/Almagest.Api/

RUN dotnet publish src/Almagest.Api/Almagest.Api.csproj -c Release -o /app --no-restore

# --- Runtime: no SDK, no source, just the published app ---------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app .

# The base image already runs as the non-root "app" user and listens on 8080.
USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "Almagest.Api.dll"]
