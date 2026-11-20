FROM scratch AS app-source
COPY .dockerignore Dockerfile GitMeadow.slnx LICENSE README.md THIRD-PARTY-NOTICES compose.yaml global.json ./
COPY src/GitMeadow/ src/GitMeadow/
COPY tests/GitMeadow.Tests/ tests/GitMeadow.Tests/

# Build the framework-dependent app with the builder's native SDK.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100 AS build
WORKDIR /src
COPY --from=app-source /global.json ./
COPY --from=app-source /src/GitMeadow/GitMeadow.csproj /src/GitMeadow/packages.lock.json src/GitMeadow/
RUN dotnet restore src/GitMeadow/GitMeadow.csproj --locked-mode
COPY --from=app-source /src/GitMeadow/ src/GitMeadow/
ARG APP_VERSION
RUN dotnet publish src/GitMeadow/GitMeadow.csproj -c Release --no-restore -p:UseAppHost=false -o /out \
    ${APP_VERSION:+-p:InformationalVersion="$APP_VERSION"}

FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine-extra AS runtime
RUN apk add --no-cache git openssh-client-default curl \
    && install -d -o app -g app /workspaces \
    && install -d -m 0700 -o app -g app /home/app /home/app/.ssh

# Bundle the source and Alpine packaging for the installed Git binary.
FROM runtime AS git-source
RUN apk add --no-cache abuild jq xz
WORKDIR /git-source
RUN git_package=$(apk query --installed --format json --fields version,commit git) \
    && git_version=$(printf '%s' "$git_package" | jq -er '.[0].version') \
    && aports_commit=$(printf '%s' "$git_package" | jq -er '.[0].commit') \
    && curl --fail --silent --show-error --location --retry 2 \
        "https://codeload.github.com/alpinelinux/aports/tar.gz/$aports_commit" -o /tmp/aports.tar.gz \
    && tar -xzf /tmp/aports.tar.gz --strip-components=3 "aports-$aports_commit/main/git" \
    && SRCDEST="$PWD" srcdir=/tmp/git-source-links abuild -F fetch \
    && tar -xOf "git-${git_version%-r*}.tar.xz" "git-${git_version%-r*}/COPYING" > COPYING \
    && printf 'git=%s\naports=%s\n' "$git_version" "$aports_commit" > package.txt

FROM runtime
WORKDIR /app
COPY --from=build /out .
COPY --from=app-source / /usr/share/doc/git-meadow/app-source/
COPY --from=git-source /git-source/ /usr/share/doc/git-meadow/git-source/
COPY LICENSE THIRD-PARTY-NOTICES /usr/share/doc/git-meadow/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    GitMeadow__Workspace=/workspaces \
    HOME=/home/app
EXPOSE 8080
USER app
HEALTHCHECK --interval=20s --timeout=3s --start-period=10s \
    CMD ["curl", "--fail-early", "--fail", "--silent", "--output", "/dev/null", "http://127.0.0.1:8080/healthz", "--output", "/dev/null", "http://127.0.0.1:8080/_framework/blazor.web.js"]
ENTRYPOINT ["dotnet", "GitMeadow.dll"]
