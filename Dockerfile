FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY HackYeahBackend/HackYeahBackend.csproj HackYeahBackend/
RUN dotnet restore HackYeahBackend/HackYeahBackend.csproj

COPY HackYeahBackend/ HackYeahBackend/
RUN dotnet publish HackYeahBackend/HackYeahBackend.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_EnableDiagnostics=0
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY --from=build /app/publish .
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "HackYeahBackend.dll"]
