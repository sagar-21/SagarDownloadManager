# Sagar Download Manager — License Server
# Build:   docker build -t dm-license-server .
# Run:     docker run -p 8080:8080 --env-file .env dm-license-server

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["DM.LicenseServer/DM.LicenseServer.csproj", "DM.LicenseServer/"]
RUN dotnet restore "DM.LicenseServer/DM.LicenseServer.csproj"
COPY DM.LicenseServer/ DM.LicenseServer/
WORKDIR "/src/DM.LicenseServer"
RUN dotnet publish "DM.LicenseServer.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DM.LicenseServer.dll"]
