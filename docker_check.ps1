rd obj -Force -Recurse -Confirm:$false
rd bin -Force -Recurse -Confirm:$false
docker build -t todo-2-gh-issue -f Dockerfile .
docker run -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 --env-file debug.env todo-2-gh-issue