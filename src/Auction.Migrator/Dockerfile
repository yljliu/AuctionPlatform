FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Auction.Domain/*.csproj src/Auction.Domain/
COPY src/Auction.Contracts/*.csproj src/Auction.Contracts/
COPY src/Auction.Infrastructure/*.csproj src/Auction.Infrastructure/
COPY src/Auction.Migrator/*.csproj src/Auction.Migrator/
RUN dotnet restore src/Auction.Migrator
COPY src/ src/
RUN dotnet publish src/Auction.Migrator -c Release -o /out --no-restore

# aspnet, not runtime: Auction.Contracts brings the ASP.NET framework (spec 09 observability extension).
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Auction.Migrator.dll"]