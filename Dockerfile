FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /app
COPY . .
RUN dotnet restore
RUN dotnet build -c Release
CMD ["dotnet", "run", "--project", "TGbot.csproj", "-c", "Release"]