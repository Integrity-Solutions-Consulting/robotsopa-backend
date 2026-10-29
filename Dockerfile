FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SriAutomation.Api -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
RUN apt-get update && apt-get install -y --no-install-recommends curl fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
HEALTHCHECK --interval=15s --timeout=5s CMD curl --fail http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "SriAutomation.Api.dll"]
