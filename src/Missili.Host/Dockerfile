# 开发与发布均使用仓库根目录为构建上下文，最终宿主保持非 root 运行。
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN mkdir -p /app/data && chown $APP_UID /app/data
USER $APP_UID
WORKDIR /app
EXPOSE 8080

# 官方插件由共享 MSBuild 清单决定，不在 Dockerfile 重复维护工程清单。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /source
COPY ["Directory.Build.props", "NuGet.Config", "./"]
COPY build/ build/
COPY third-party/ third-party/
COPY src/ src/
COPY plugins/ plugins/
RUN dotnet restore src/Missili.Host/Missili.Host.csproj
RUN dotnet build src/Missili.Host/Missili.Host.csproj -c $BUILD_CONFIGURATION --no-restore -o /app/build

# 同一发布目标同时准备宿主、官方插件及适用许可材料。
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish src/Missili.Host/Missili.Host.csproj -c $BUILD_CONFIGURATION --no-restore -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Missili.Host.dll"]
