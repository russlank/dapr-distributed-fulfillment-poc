# One multi-stage build for every role; Compose builds one image per role (PROJECT argument).
# BUILD_FLAVOR=drill compiles test-only failure injection in; the default release flavor cannot.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
ARG PROJECT
ARG BUILD_FLAVOR=release
ARG SOURCE_REVISION_ID=
ARG SOURCE_BRANCH=
ARG REPOSITORY_URL=
ARG SOURCE_TREE_STATE=unknown
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY . .
RUN test -n "$PROJECT" \
 && FAILPOINTS=$([ "$BUILD_FLAVOR" = "drill" ] && echo true || echo false) \
 && dotnet publish "$PROJECT/$PROJECT.csproj" -c Release -o /app \
      -p:BuildFlavor="$BUILD_FLAVOR" -p:EnableFailpoints="$FAILPOINTS" \
      -p:SourceRevisionId="$SOURCE_REVISION_ID" -p:SourceBranch="$SOURCE_BRANCH" -p:RepositoryUrl="$REPOSITORY_URL" -p:SourceTreeState="$SOURCE_TREE_STATE" \
 && echo "$PROJECT" > /app/.project

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health/live HTTP/1.0\r\n\r\n" >&3 && head -1 <&3 | grep -q " 200 "'
# Arguments (for example the order API's "migrate" command) are passed through to the app.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$(cat /app/.project).dll\" \"$@\"", "--"]
