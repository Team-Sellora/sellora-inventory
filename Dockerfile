FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Sellora.InventoryService.sln
RUN dotnet publish src/Sellora.InventoryService.Api/Sellora.InventoryService.Api.csproj --configuration Release --output /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Sellora.InventoryService.Api.dll"]