# AGENTS.md

Guidance for AI coding agents working in this repository. Human-readable docs live in
[README.md](README.md) — read it first for architecture, env vars and deployment.

## ⚠️ CRITICAL: `dotnet restore` must be run by the human

**This agent's sandbox breaks .NET's TLS stack, so NuGet restore fails with endless
retry/timeout loops (and any manual TLS tweaks make it worse, not better).**

Therefore:

1. **You MUST NOT run `dotnet restore` yourself.** It will burn time and never succeed.
2. **You MUST NOT try workarounds.** Do not: retry in a loop, set `DOTNET_SYSTEM_NET_*`
   variables, disable certificate validation, vendor packages into the repo, edit
   `project.assets.json` / `obj/` by hand, or comment out `PackageReference`s to fake a
   restore.
3. **When restore is needed** (new/changed `PackageReference`, first clean checkout,
   missing assets), do all your file/code edits first, then **stop and ask the user to run**:

   ```bash
   dotnet restore src/Uts.Api
   ```

   and wait for them to confirm it finished before attempting a build.

Restore may be possible add-hoc outside the sandbox (e.g. the user's own terminal or the
Docker build) — leave it to the user to decide. Building (`dotnet build`) works fine
whenever `obj/project.assets.json` already exists or no new packages were added.

## Other ground rules

- **Run the app** with `dotnet run --project src/Uts.Api` (or `dotnet src/Uts.Api/bin/.../Uts.Api.dll`
  — `UseAppHost` is disabled, there is no standalone `.exe`). Startup applies DB migrations;
  for a live database, first `cd deploy && docker compose up -d db`.
- **Do not enable `AUTH__DEVAUTH` or other dev-only flags in anything committed** (compose,
  workflows); they belong in the git-ignored `local.env` only.
- **Postgres arrays**: Dapper needs the custom handler (`Db/StringArrayTypeHandler.cs`) — it
  is registered in `Program.cs`; don't bypass it.
- Never edit generated files (`obj/`, `bin/`, `wwwroot/dist` from the Vite build).
