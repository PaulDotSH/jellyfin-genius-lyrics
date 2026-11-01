# Project agent instructions

## Project

Jellyfin server plugin, "Genius Lyrics". It adds a lyrics provider for audio items that looks up lyrics on Genius (scraped) and LRCLIB, and a weekly scheduled task that backfills lyrics for the library. Targets Jellyfin 10.11.x on .NET 9. Status: working plugin with ongoing fixes (rate limiting, matching, backfill reliability).

## Stack and layout

- C# on .NET 9 (`net9.0`), SDK-style project `GeniusLyricsPlugin.csproj`. References Jellyfin.Controller and Jellyfin.Model 10.11.11 (runtime assets excluded, the server supplies them) and HtmlAgilityPack.
- `Plugin.cs`: plugin entry point and config page registration. `PluginServiceRegistrator.cs`: DI registrations.
- `Providers/GeniusLyricsProvider.cs`: the lyrics provider. `Tasks/LyricsBackfillTask.cs`: the scheduled backfill task.
- `Services/`: Genius scraper (`ScraperService`, `GeniusParser`), `LrcLibClient`, `SongMatcher`, `RequestThrottle`, `LyricsLookupResult`.
- `Configuration/`: `PluginConfiguration.cs` and `configPage.html` (embedded resource).
- `tests/GeniusLyricsPlugin.Tests/`: xUnit tests, excluded from the plugin build.
- `bin/` and `obj/` are build output. Do not edit them.

## Commands

- Build: `dotnet build GeniusLyricsPlugin.csproj`
- Test: `dotnet test tests/GeniusLyricsPlugin.Tests -v q`

Run the relevant checks before declaring work done.

## Coding style and rules

- Follow existing patterns and match the surrounding code.
- Do not over-rely on OOP when a simpler structure fits.
- Do not add helper functions when an equivalent already exists in a library
  that is already in use.
- Prefer the standard library and existing dependencies over new ones.
- Do not add comments unless asked.
- The scraper and LRCLIB client are singletons shared by the provider and the
  backfill task (one HTTP client, one cache, one rate limiter). Do not create a
  second client for them.
- `Plugin.CurrentConfiguration` falls back to defaults when the plugin is not
  loaded (tests and local harnesses). Keep that working.
- Commit or push only when asked. Never add AI co-author trailers (e.g.
  `Co-Authored-By: Claude`) to commits.

## Testing and verification

- Write tests before implementation where practical.
- When a test fails, fix the code, not the test, unless the test asserts the
  wrong behavior. Never weaken a test just to make it pass.
- Static types, linters, and checkers are part of "done".
- Parser and matcher behavior is covered by `GeniusParserTests.cs` and
  `SongMatcherTests.cs`; extend those for changes to either.

## Output-heavy commands

- **Delegate output-heavy commands to a cheap model.** When a command's output
  is large but the answer you need is small (parsing logs, sifting build or CI
  output, scanning long dumps), prefer handing it to a cheap subagent and
  taking back only the conclusion: DeepSeek via the `deepseek` MCP
  (`run_task`) or Claude Haiku. Give it the exact command and what to report;
  it reads, it does not edit. This needs no explicit opt-in.
- **Test runs print failures, not passes.** Run suites in a mode that hides
  passing tests and shows only failures plus the summary: e.g. `cargo test -q`,
  `cargo nextest run --status-level fail`, `pytest -q`, `vitest --reporter=dot`,
  `go test ./...` without `-v`, `dotnet test -v q`. If the runner has no such
  mode, filter its output down to the failures. Never pull hundreds of passing
  lines into context.

## Planning

- For large tasks, plan first with a strong model, then implement with cheaper
  models to save tokens.
- State the goal and the stopping condition before starting.

## Subagent usage

These rules are always in effect, even when the `dispatching-parallel-agents`
skill is not loaded. If you do dispatch subagents, follow them; the skill has
the full workflow.

- **Explicit opt-in only.** Never dispatch, spawn, or delegate to subagents
  unless the developer explicitly asks for it in the current task (e.g. "use
  subagents", "dispatch these in parallel", "delegate this"). A task looking
  parallelizable is not authorization, and neither is a past instruction. If
  parallel work would clearly help, ask once; absent a yes, do the work yourself,
  sequentially. Exception: delegating an output-heavy command to a cheap model
  (see "Output-heavy commands") needs no opt-in.
- **Cheap models for subagents.** The coordinating session runs the expensive
  model; helpers run a cheap/fast one: ChatGPT Luna, DeepSeek v4.1 Flash, Claude
  Sonnet, or Muse-Spark. Pick the cheapest that can do the job, never silently a
  premium model, and state the chosen model in the dispatch and the summary.
- **Poll at the harness cadence.** Polling too often destroys prompt-cache
  retention. Claude Code: check every 4 minutes. Codex and opencode running
  OpenAI models: check every 25 minutes, otherwise wait for completion, and do
  not poll faster. Manual polling only — no tight auto-poll loops. If a result
  arrives earlier, use it immediately.
