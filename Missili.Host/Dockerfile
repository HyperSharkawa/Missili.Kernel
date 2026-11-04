# 请参阅 https://aka.ms/customizecontainer 以了解如何自定义调试容器，以及 Visual Studio 如何使用此 Dockerfile 生成映像以更快地进行调试。

# 此阶段用于在快速模式(默认为调试配置)下从 VS 运行时
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN mkdir -p /app/data && chown $APP_UID /app/data
USER $APP_UID
WORKDIR /app
EXPOSE 8080


# 此阶段用于生成服务项目
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["NuGet.Config", "./"]
COPY ["third-party/nuget/", "third-party/nuget/"]
COPY ["Missili.Abstractions/Missili.Abstractions.csproj", "Missili.Abstractions/"]
COPY ["Missili.Kernel/Missili.Kernel.csproj", "Missili.Kernel/"]
COPY ["Missili.Matrix/Missili.Matrix.csproj", "Missili.Matrix/"]
COPY ["Missili.Models/Missili.Models.csproj", "Missili.Models/"]
COPY ["Missili.Nexus/Missili.Nexus.csproj", "Missili.Nexus/"]
COPY ["Missili.Persistence/Missili.Persistence.csproj", "Missili.Persistence/"]
COPY ["Missili.Adapters.OneBot/Missili.Adapters.OneBot.csproj", "Missili.Adapters.OneBot/"]
COPY ["Missili.Providers.OpenAI/Missili.Providers.OpenAI.csproj", "Missili.Providers.OpenAI/"]
COPY ["Missili.Providers.Anthropic/Missili.Providers.Anthropic.csproj", "Missili.Providers.Anthropic/"]
COPY ["Missili.Host/Missili.Host.csproj", "Missili.Host/"]
RUN dotnet restore "./Missili.Host/Missili.Host.csproj"
COPY . .
WORKDIR "/src/Missili.Host"
RUN dotnet build "./Missili.Host.csproj" -c $BUILD_CONFIGURATION -o /app/build

# 此阶段用于发布要复制到最终阶段的服务项目
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Missili.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# 此阶段在生产中使用，或在常规模式下从 VS 运行时使用(在不使用调试配置时为默认值)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Missili.Host.dll"]
