FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 1006

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["MessengerService/MessengerService.csproj", "MessengerService/"]
RUN dotnet restore "MessengerService/MessengerService.csproj"
COPY MessengerService/ MessengerService/
WORKDIR /src/MessengerService
RUN dotnet publish "MessengerService.csproj" -c Release -o /app/publish --no-restore

FROM runtime AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:1006
USER $APP_UID
ENTRYPOINT ["dotnet", "MessengerService.dll"]
