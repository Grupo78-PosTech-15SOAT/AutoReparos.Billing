FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AutoReparos.Billing.slnx ./
COPY AutoReparos.Billing.Api/AutoReparos.Billing.Api.csproj AutoReparos.Billing.Api/
COPY AutoReparos.Billing.Application/AutoReparos.Billing.Application.csproj AutoReparos.Billing.Application/
COPY AutoReparos.Billing.Domain/AutoReparos.Billing.Domain.csproj AutoReparos.Billing.Domain/
COPY AutoReparos.Billing.Infrastructure/AutoReparos.Billing.Infrastructure.csproj AutoReparos.Billing.Infrastructure/
RUN dotnet restore AutoReparos.Billing.slnx
COPY . .
RUN dotnet publish AutoReparos.Billing.Api/AutoReparos.Billing.Api.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AutoReparos.Billing.Api.dll"]
