param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "src\GitlabNotifier.Runner\GitlabNotifier.Runner.csproj"

dotnet publish $projectPath --configuration Release --output $OutputDir
exit $LASTEXITCODE
