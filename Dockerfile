FROM mono:latest
WORKDIR /app
COPY . .
RUN nuget restore TGbot.csproj -PackagesDirectory ./packages
RUN msbuild TGbot.csproj /p:Configuration=Release
CMD ["mono", "./bin/Release/TGbot.exe"]