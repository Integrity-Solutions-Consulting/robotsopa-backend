FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SriAutomation.Api -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
RUN apt-get update && apt-get install -y --no-install-recommends curl fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
COPY config/sri-portal.json /app/config/sri-portal.json
RUN mkdir -p /app/storage /app/downloads /app/sessions /app/credentials && chown -R app:app /app/storage /app/downloads /app/sessions /app/credentials
ENV ASPNETCORE_URLS=http://+:8080 \
    SRI_STATE_PATH=/app/storage \
    SRI_OUTPUT_PATH=/app/downloads \
    SRI_SESSION_PATH=/app/sessions \
    SRI_CREDENTIAL_PATH=/app/credentials \
    SRI_PORTAL_PROFILE=/app/config/sri-portal.json
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD curl --fail --silent http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "SriAutomation.Api.dll"]
