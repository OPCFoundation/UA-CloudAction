#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# The .NET 8+ ASP.NET base images listen on 8080 by default. Force the app to
# listen on port 80 so it matches the Container Apps ingress/startup probe (TLS is
# terminated at the ingress there). HTTPS (443) is exposed for other hosts that
# supply a certificate at runtime (e.g. Visual Studio mounts the dev cert).
ENV ASPNETCORE_HTTP_PORTS=80
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["UA-CloudAction.csproj", "."]
RUN dotnet restore "./UA-CloudAction.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "UA-CloudAction.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "UA-CloudAction.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "UA-CloudAction.dll"]