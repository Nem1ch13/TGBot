FROM mono:latest
WORKDIR /app
COPY . .
RUN nuget restore TGbot.sln
RUN msbuild TGbot.sln /p:Configuration=Release
CMD ["mono", "./bin/Release/TGbot.exe"]