# IMPORTANTE: si tu QuinielaBackend.csproj dice net9.0 (en vez de net8.0),
# cambia los dos "8.0" de abajo por "9.0".

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "QuinielaBackend.dll"]
