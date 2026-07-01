FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Nexiffy.csproj .
RUN dotnet restore Nexiffy.csproj

COPY . .
RUN dotnet publish Nexiffy.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5200
ENTRYPOINT ["dotnet", "Nexiffy.dll"]
