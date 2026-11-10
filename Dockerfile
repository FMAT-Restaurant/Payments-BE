FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY PaymentsModule/PaymentsModule.csproj PaymentsModule/
RUN dotnet restore PaymentsModule/PaymentsModule.csproj
COPY PaymentsModule/ PaymentsModule/
RUN dotnet publish PaymentsModule/PaymentsModule.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PaymentsModule.dll"]
