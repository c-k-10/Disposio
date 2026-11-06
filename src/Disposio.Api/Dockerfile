# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Disposio.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

# Laufzeit
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Disposio.Api.dll"]
