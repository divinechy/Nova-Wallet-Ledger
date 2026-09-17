FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/NovaWalletLedger.Api/NovaWalletLedger.Api.csproj src/NovaWalletLedger.Api/
RUN dotnet restore src/NovaWalletLedger.Api/NovaWalletLedger.Api.csproj

COPY src/NovaWalletLedger.Api/ src/NovaWalletLedger.Api/
WORKDIR /src/src/NovaWalletLedger.Api
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "NovaWalletLedger.Api.dll"]
