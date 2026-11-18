FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY DaprCompat.App/DaprCompat.App.csproj DaprCompat.App/
RUN dotnet restore DaprCompat.App/DaprCompat.App.csproj
COPY DaprCompat.App/ DaprCompat.App/
RUN dotnet publish DaprCompat.App/DaprCompat.App.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "DaprCompat.App.dll"]
