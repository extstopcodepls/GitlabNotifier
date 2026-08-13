# AGENTS.md

## Scope
These instructions apply to the entire repository rooted at `D:\Projects\GitlabNotifier`.

## Repository Layout
- `GitlabNotifier.sln` is the solution entry point.
- `src/GitlabNotifier.Runner` contains the executable worker application.
- `src/GitlabNotifier.Data` contains the EF Core data layer and entities.
- `run.ps1` runs the runner project in `Release`.
- `publish.ps1` publishes the runner project to a caller-provided output directory.

## Stack
- .NET 10
- EF Core with SQLite
- Refit for GitLab API access
- Windows desktop notifications

## Build And Run
- Build solution: `dotnet build GitlabNotifier.sln -c Release`
- Run app: `.\run.ps1`
- Publish app: `.\publish.ps1 -OutputDir <path>`

## Configuration
- Runtime configuration lives in `src/GitlabNotifier.Runner/appsettings.json`.
- The runner expects:
  - `ConnectionStrings:GitlabNotifier`
  - `Runner:PollIntervalSeconds`
  - `Runner:NotificationReminderIntervalSeconds`
  - `Gitlab:Url`
  - `Gitlab:ApiKey`

## Security
- Treat `Gitlab:ApiKey` as a secret. Do not commit real credentials.
- If configuration changes are needed, prefer sanitized examples or local-only values.
- Flag any checked-in secrets immediately before making unrelated changes.

## Code Guidance
- Keep changes scoped to the relevant project: runner logic in `GitlabNotifier.Runner`, persistence logic in `GitlabNotifier.Data`.
- Follow the existing hosting pattern in `Program.cs`: configuration binding, DI registration, hosted services, then startup initialization.
- Prefer extending existing services instead of introducing new abstraction layers unless duplication or coupling justifies it.
- Preserve nullable reference type behavior and implicit usings already enabled in both projects.

## Verification
- For code changes, prefer targeted verification first:
  - `dotnet build GitlabNotifier.sln -c Release`
- If behavior changes touch polling, notifications, or persistence, add or run the narrowest meaningful validation available.

## Notes For Agents
- This repository currently has no test project. Do not claim test coverage that does not exist.
- The runner is Windows-targeted (`net10.0-windows10.0.19041.0`), so keep OS-specific behavior compatible with Windows.
