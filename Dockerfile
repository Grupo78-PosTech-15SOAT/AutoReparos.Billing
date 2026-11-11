FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AutoReparos.Cobranca.slnx ./
COPY AutoReparos.Cobranca.Api/AutoReparos.Cobranca.Api.csproj AutoReparos.Cobranca.Api/
COPY AutoReparos.Cobranca.Application/AutoReparos.Cobranca.Application.csproj AutoReparos.Cobranca.Application/
COPY AutoReparos.Cobranca.Domain/AutoReparos.Cobranca.Domain.csproj AutoReparos.Cobranca.Domain/
COPY AutoReparos.Cobranca.Infrastructure/AutoReparos.Cobranca.Infrastructure.csproj AutoReparos.Cobranca.Infrastructure/
RUN dotnet restore AutoReparos.Cobranca.slnx
COPY . .
RUN dotnet publish AutoReparos.Cobranca.Api/AutoReparos.Cobranca.Api.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AutoReparos.Cobranca.Api.dll"]
