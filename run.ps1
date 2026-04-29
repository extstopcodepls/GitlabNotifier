$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "src\GitlabNotifier.Runner\GitlabNotifier.Runner.csproj"

dotnet run --project $projectPath --configuration Release -- @args
exit $LASTEXITCODE
