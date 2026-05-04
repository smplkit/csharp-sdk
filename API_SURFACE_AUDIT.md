```
Repo:         /Users/mike/projects/csharp-sdk
Language:     C# (.NET 8.0, package Smplkit.Sdk, namespace Smplkit)
Released:     v3.5.8 @ f06a6271efcb610ae97a784d8a0a04fccc13b2f2 ("fix: C# SDK Overhaul")
HEAD:         970818f0d197fe205802f62f606ab5ead09ca00d ("fix: typed-model deserialization preserves snake_case config keys")
```

Notes on naming map: C# uses **PascalCase** for public members and an **Options object** for constructor settings (`SmplClientOptions { ApiKey, Environment, Service }`), not positional args. Async methods use the `Async` suffix (`SaveAsync`, `RefreshAsync`, `InstallAsync`). The runtime client is `SmplClient`; management plane is `client.Manage` (== `SmplManagementClient`). No public-API surface changed between v3.5.8 and HEAD — the 5 unreleased commits touch only internal helpers (`LiveConfigProxy.Into<T>`, `JsonOptions`, `SharedWebSocket`, CI workflows, tests).

## Configuration

| Surface | HEAD | Released (v3.5.8) |
|---|---|---|
| Client construction `(api_key, environment, service)` | [SmplClient.cs:65](src/Smplkit/SmplClient.cs:65) `public SmplClient(SmplClientOptions options)` + [SmplClientOptions.cs:13–35](src/Smplkit/SmplClientOptions.cs:13) `ApiKey`, `Environment`, `Service` properties | identical (verified `git show v3.5.8:src/Smplkit/SmplClient.cs`) |
| `close()` / `Dispose` | [SmplClient.cs:204](src/Smplkit/SmplClient.cs:204) `public void Dispose()` (class declared `: IDisposable` at line 21) | identical |
| `client.config.get(key)` | [Config/ConfigClient.cs:61](src/Smplkit/Config/ConfigClient.cs:61) `public LiveConfigProxy Get(string id)` — returns `LiveConfigProxy : IReadOnlyDictionary<string, object?>` ([LiveConfigProxy.cs:26](src/Smplkit/Config/LiveConfigProxy.cs:26)) so it behaves as a "map of resolved values"; also typed overload `public T Get<T>(string id)` at line 81 | identical signatures (Into<T> helper changed snake_case handling internally) |
| `client.manage.config.new(key, name=, description=, parent=?)` | [Management/ConfigsClient.cs:27](src/Smplkit/Management/ConfigsClient.cs:27) `public Smplkit.Config.Config New(string id, string? name = null, string? description = null, object? parent = null)` (parent accepts a string id or another `Config`) | identical |
| Active-record `set_string` / `set_number` / `set_boolean` (+ env override) | [Config/Models.cs:101–110](src/Smplkit/Config/Models.cs:101) `public void SetString(string name, string value, string? description = null, string? environment = null)`, `SetNumber(string, double, ..., string? environment = null)`, `SetBoolean(string, bool, ..., string? environment = null)` (also `SetJson`). The optional `environment` arg is the per-environment override variant | identical |
| `save()` / `delete()` / `id` on the handle | [Config/Models.cs:58](src/Smplkit/Config/Models.cs:58) `public async Task SaveAsync(CancellationToken ct = default)`, line 72 `public Task DeleteAsync(CancellationToken ct = default)`, line 12 `public string? Id { get; internal set; }` | identical |
| `client.manage.config.get(key)` | [Management/ConfigsClient.cs:71](src/Smplkit/Management/ConfigsClient.cs:71) `public async Task<Smplkit.Config.Config> GetAsync(string id, CancellationToken ct = default)` | identical |
| `client.manage.config.list()` | [Management/ConfigsClient.cs:51](src/Smplkit/Management/ConfigsClient.cs:51) `public async Task<List<Smplkit.Config.Config>> ListAsync(CancellationToken ct = default)` | identical |
| `client.manage.config.delete(key)` | [Management/ConfigsClient.cs:81](src/Smplkit/Management/ConfigsClient.cs:81) `public async Task DeleteAsync(string id, CancellationToken ct = default)` | identical |
| Two-level inheritance (third level rejected) | Client-side support: `Parent` field at [Config/Models.cs:21](src/Smplkit/Config/Models.cs:21) and `parent` argument on `New(...)`. The depth limit is server-enforced; the SDK passes `parent` through faithfully and surfaces server validation errors via `ApiExceptionMapper`. | identical |

**Verdict: COMPLETE** — every Configuration surface in the spec exists at both HEAD and v3.5.8.

## Flags

| Surface | HEAD | Released (v3.5.8) |
|---|---|---|
| Runtime: `boolean_flag(key, default)` | [Flags/FlagsClient.cs:81](src/Smplkit/Flags/FlagsClient.cs:81) `public BooleanFlag BooleanFlag(string id, bool defaultValue)` | identical |
| Runtime: `string_flag(...)` | [Flags/FlagsClient.cs:104](src/Smplkit/Flags/FlagsClient.cs:104) `public StringFlag StringFlag(string id, string defaultValue)` | identical |
| Runtime: `number_flag(...)` | [Flags/FlagsClient.cs:127](src/Smplkit/Flags/FlagsClient.cs:127) `public NumberFlag NumberFlag(string id, double defaultValue)` (and `JsonFlag JsonFlag(...)` at line 150 as a bonus 4th type) | identical |
| Handle `.get()` and `.get(context=[...])` | Per-type typed Get on `BooleanFlag`/`StringFlag`/`NumberFlag`/`JsonFlag` in [Flags/Models.cs:305](src/Smplkit/Flags/Models.cs:305): e.g. `public new bool Get(IReadOnlyList<Context>? context = null)` (StringFlag.Get→string at 329, NumberFlag.Get→double at 353, JsonFlag.Get→Dictionary at 381). Base `Flag.Get` at [Flags/Models.cs:276](src/Smplkit/Flags/Models.cs:276). | identical |
| `client.flags.stats()` exposing `cache_hits` / `cache_misses` | [Flags/FlagsClient.cs:243](src/Smplkit/Flags/FlagsClient.cs:243) `public FlagStats Stats => new(_cache.CacheHits, _cache.CacheMisses);` — `FlagStats(int CacheHits, int CacheMisses)` defined at [Flags/Models.cs:402](src/Smplkit/Flags/Models.cs:402). C#-idiomatic property, not a method. | identical |
| `client.flags.refresh()` | [Flags/FlagsClient.cs:228](src/Smplkit/Flags/FlagsClient.cs:228) `public async Task RefreshAsync(CancellationToken ct = default)` | identical |
| Ambient context: `client.set_context([Context(...)])` | [SmplClient.cs:136](src/Smplkit/SmplClient.cs:136) `public IDisposable SetContext(IEnumerable<Context> contexts)` (returns scope that reverts on dispose); convenience overload `SetContext(Context context)` at line 152 | identical |
| Ambient context: `client.flags.set_context_provider(callback)` | [Flags/FlagsClient.cs:176](src/Smplkit/Flags/FlagsClient.cs:176) `public void SetContextProvider(Func<IReadOnlyList<Context>> provider)` | identical |
| Management: `new_<boolean\|string\|number>_flag` (active-record creator) | [Management/FlagsClient.cs:26](src/Smplkit/Management/FlagsClient.cs:26) `public BooleanFlag NewBooleanFlag(string id, bool defaultValue, string? name = null, string? description = null)`; `NewStringFlag` at line 46; `NewNumberFlag` at line 62; `NewJsonFlag` at line 78 | identical |
| Environment-scoped override + `save()` + `delete()` on the handle | [Flags/Models.cs:138](src/Smplkit/Flags/Models.cs:138) `SetEnvironmentDefault(string envKey, object? defaultValue)`, `SetEnvironmentEnabled(envKey, enabled)` (line 123), `SetDefault(value, environment=null)` (line 174), `EnableRules/DisableRules(environment=null)` (lines 205, 221), `AddRule(builtRule)` (line 95). Persistence via `SaveAsync` (line 73), `DeleteAsync` (line 261). | identical |
| `client.manage.flags.get(id)` | [Management/FlagsClient.cs:104](src/Smplkit/Management/FlagsClient.cs:104) `public async Task<Flag> GetAsync(string id, CancellationToken ct = default)` | identical |
| `client.manage.flags.list()` | [Management/FlagsClient.cs:94](src/Smplkit/Management/FlagsClient.cs:94) `public async Task<List<Flag>> ListAsync(CancellationToken ct = default)` | identical |
| `client.manage.flags.delete(id)` | [Management/FlagsClient.cs:113](src/Smplkit/Management/FlagsClient.cs:113) `public async Task DeleteAsync(string id, CancellationToken ct = default)` | identical |
| `client.manage.contexts.register([Context])` | [Management/ManagementClient.cs:249](src/Smplkit/Management/ManagementClient.cs:249) `public async Task RegisterAsync(IEnumerable<Smplkit.Context> contexts, bool flush = false, CancellationToken ct = default)` (and single-context overload at line 245) | identical |
| `client.manage.contexts.flush()` | [Management/ManagementClient.cs:260](src/Smplkit/Management/ManagementClient.cs:260) `public async Task FlushAsync(CancellationToken ct = default)` | identical |
| Public `Context` type/constructor `(type, key, attrs)` | [Context.cs:61](src/Smplkit/Context.cs:61) `public Context(string type, string key, Dictionary<string, object?>? attributes = null, string? name = null)` — class is `public sealed` at line 26 | identical |

**Verdict: COMPLETE** — all flag surfaces present at both HEAD and v3.5.8. (`Stats` is a property rather than a method per C# convention, but it exposes the required `CacheHits`/`CacheMisses` shape.)

## Logging

| Surface | HEAD | Released (v3.5.8) |
|---|---|---|
| `client.logging.install()` (method exists) | [Logging/LoggingClient.cs:93](src/Smplkit/Logging/LoggingClient.cs:93) `public async Task InstallAsync(CancellationToken ct = default)` | identical |
| install() **auto-discovers pre-existing native loggers** | **NOT MET.** `InstallAsync` at line 93 calls `AutoLoadAdapters()` (line 247), which only instantiates the two built-in adapter classes via reflection (`MicrosoftLoggingAdapter`, `SerilogAdapter`). It then calls `DiscoverAll()` (line 213) which delegates to each adapter's `Discover()`. But neither adapter enumerates a global logger registry: `MicrosoftLoggingAdapter.Discover()` ([Adapters/MicrosoftLoggingAdapter.cs:49](src/Smplkit/Logging/Adapters/MicrosoftLoggingAdapter.cs:49)) only returns entries from its own `_loggers` dictionary, which is populated only via `GetOrCreateLogger(...)` — i.e. via loggers the user explicitly created through `adapter.Factory`. `SerilogAdapter.Discover()` ([Adapters/SerilogAdapter.cs:48](src/Smplkit/Logging/Adapters/SerilogAdapter.cs:48)) only returns entries from `_switches`, populated only via explicit `GetOrCreateSwitch(name)` calls. Searches: `grep 'GlobalLoggerFactory\|getLoggerRepository\|Log\.Logger' src/Smplkit/Logging/` returned only the doc-comment example at SerilogAdapter.cs:12. There is no enumeration of `Microsoft.Extensions.Logging.ILoggerFactory.GetType().GetField(...)` or any equivalent global-registry probe. To get any loggers registered, the application must wrap its `ILoggerFactory` with `adapter.Factory` (MEL) or wire `adapter.GetOrCreateSwitch(...)` into Serilog config. | identical (same code at v3.5.8 — verified `git show v3.5.8:src/Smplkit/Logging/Adapters/MicrosoftLoggingAdapter.cs` lines 40–60) |
| install() **applies server-managed levels back onto loggers** | MET. [Logging/LoggingClient.cs:120](src/Smplkit/Logging/LoggingClient.cs:120) `ApplyLevels(loggers)` invoked after fetching loggers from the management plane; `ApplyLevels` at line 333 calls `adapter.ApplyLevel(logger.Id!, logger.Level.Value)` per registered adapter, which mutates the underlying `MsLogLevel` ([MicrosoftLoggingAdapter.cs:60](src/Smplkit/Logging/Adapters/MicrosoftLoggingAdapter.cs:60)) or `LoggingLevelSwitch.MinimumLevel` ([SerilogAdapter.cs:59](src/Smplkit/Logging/Adapters/SerilogAdapter.cs:59)). Caveat: this only affects loggers the adapter is tracking — see auto-discovery row above. | identical |
| `client.logging.refresh()` | **NOT FOUND.** `grep -n 'public.*[Rr]efresh' src/Smplkit/Logging/LoggingClient.cs` → 0 hits. Public surface of `Smplkit.Logging.LoggingClient` is exhaustively: `RegisterAdapter`, `InstallAsync`, two `OnChange` overloads (verified by `grep -n 'public ' src/Smplkit/Logging/LoggingClient.cs`). Compare `Smplkit.Config.ConfigClient.RefreshAsync` ([Config/ConfigClient.cs:135](src/Smplkit/Config/ConfigClient.cs:135)) and `Smplkit.Flags.FlagsClient.RefreshAsync` ([Flags/FlagsClient.cs:228](src/Smplkit/Flags/FlagsClient.cs:228)) which do exist — Logging is the asymmetric one. | NOT FOUND at v3.5.8 (verified `git show v3.5.8:src/Smplkit/Logging/LoggingClient.cs \| grep 'public '` — same 4 public methods only) |
| `client.manage.loggers.new(id, managed=?)` returning a handle | [Management/LoggersClient.cs:29](src/Smplkit/Management/LoggersClient.cs:29) `public Logger New(string id, bool managed = true)`. Note: no `name` parameter — the id doubles as the display name in the C# version. | identical |
| Handle: `name`, `set_level(LogLevel.X)`, `save()`, `delete()` | [Logging/Models.cs:15](src/Smplkit/Logging/Models.cs:15) `public string Name { get; set; }`; line 89 `public void SetLevel(LogLevel level, string? environment = null)`; line 63 `public async Task SaveAsync(CancellationToken ct = default)`; line 78 `public Task DeleteAsync(CancellationToken ct = default)` | identical |
| `client.manage.loggers.get(id)` | [Management/LoggersClient.cs:55](src/Smplkit/Management/LoggersClient.cs:55) `public async Task<Logger> GetAsync(string id, CancellationToken ct = default)` | identical |
| `client.manage.loggers.list()` | [Management/LoggersClient.cs:45](src/Smplkit/Management/LoggersClient.cs:45) `public async Task<List<Logger>> ListAsync(CancellationToken ct = default)` | identical |
| `client.manage.loggers.delete(id)` | [Management/LoggersClient.cs:64](src/Smplkit/Management/LoggersClient.cs:64) `public async Task DeleteAsync(string id, CancellationToken ct = default)` | identical |
| `client.manage.loggers.flush()` | **NOT FOUND.** `grep -n 'public ' src/Smplkit/Management/LoggersClient.cs` → only `New`, `ListAsync`, `GetAsync`, `DeleteAsync`, `RegisterAsync` (line 85, takes `IEnumerable<LoggerSource>`). No `FlushAsync`. Repo-wide: `grep -rn 'public.*Flush' src/Smplkit/ \| grep -v Generated` returns one hit only — `Management/ManagementClient.cs:260` (the contexts flush). | NOT FOUND at v3.5.8 |
| `client.manage.log_groups.new(id, name=?)` | [Management/LogGroupsClient.cs:25](src/Smplkit/Management/LogGroupsClient.cs:25) `public LogGroup New(string id, string? name = null, string? group = null)` | identical |
| Handle: `set_level`, `save`, `delete` | [Logging/Models.cs:189](src/Smplkit/Logging/Models.cs:189) `public void SetLevel(LogLevel level, string? environment = null)`; line 165 `public async Task SaveAsync(...)`; line 178 `public Task DeleteAsync(...)` | identical |
| `client.manage.log_groups.get/list/delete` | [Management/LogGroupsClient.cs:49](src/Smplkit/Management/LogGroupsClient.cs:49) `GetAsync`, line 39 `ListAsync`, line 58 `DeleteAsync` | identical |
| Public `LogLevel` enum (DEBUG, INFO, WARN, ERROR) | [LogLevel.cs:6](src/Smplkit/LogLevel.cs:6) `public enum LogLevel { Trace, Debug, Info, Warn, Error, Fatal, Silent }` (note `Warn`, not `Warning`; `Info`, not `Information`) | identical |

**Verdict: PARTIAL** — all CRUD surfaces are present, but two runtime requirements are not met:

1. **`install()` does NOT auto-discover pre-existing native loggers.** Discovery is limited to loggers/switches that have flowed through the adapter's own wrapping factory (`MicrosoftLoggingAdapter.Factory`) or explicit switch lookup (`SerilogAdapter.GetOrCreateSwitch`). Bulk-registering a process's existing `Microsoft.Extensions.Logging` logger tree on `install()` — the way Python's `logging.Logger.manager.loggerDict` enumeration works — is not implemented.
2. **`client.logging.refresh()` is missing.** Config and Flags both expose `RefreshAsync`; Logging does not.

Additionally, **`client.manage.loggers.flush()` is missing** (the management contexts client has `FlushAsync`, but the loggers client does not). The auto-registration loggers buffer is flushed internally via a 30 s timer + size threshold inside the runtime client.

## Summary

- Configuration: **COMPLETE**
- Flags:         **COMPLETE**
- Logging:       **PARTIAL** — `client.logging.refresh()` missing; `install()` does not auto-discover pre-existing native loggers (adapters require explicit wrapping); `client.manage.loggers.flush()` missing.
- HEAD vs released delta:
    * **At the time of the audit:** no public-API differences between HEAD (970818f) and v3.5.8 (f06a627). The 5 unreleased commits modify only internal helpers (`LiveConfigProxy.Into<T>` snake_case JSON handling, `JsonOptions`, `SharedWebSocket` User-Agent header, test sync, CI workflows).

## Resolution (post-audit)

The Logging "PARTIAL" verdict has since been addressed on `main` (audit doc not regenerated; check `git log` for current HEAD):

- **`client.Logging.RefreshAsync`** added at [Logging/LoggingClient.cs](src/Smplkit/Logging/LoggingClient.cs) — re-fetches loggers + groups, re-applies levels onto every registered adapter, fires listeners (`Source = "manual"`) on diff.
- **`client.Manage.Loggers.FlushAsync`** + buffer added at [Management/LoggersClient.cs](src/Smplkit/Management/LoggersClient.cs); `RegisterAsync` gained an optional `flush` flag (default `true` preserves prior immediate-POST behaviour, `false` buffers). Mirrors the `Manage.Contexts` pattern.
- **`install()` auto-discovery** rebuilt around the idiomatic MEL pattern. `MicrosoftLoggingAdapter` now implements `ILoggerProvider` (every `CreateLogger` call across the host is observed → categories captured), `IConfigureOptions<LoggerFilterOptions>` (emits per-category rules), and `IOptionsChangeTokenSource<LoggerFilterOptions>` (server-pushed level changes refresh the host's filter cache, gating output across **every** registered provider). New extension [Logging/Adapters/SmplkitLoggingBuilderExtensions.cs](src/Smplkit/Logging/Adapters/SmplkitLoggingBuilderExtensions.cs) wires it all up: `services.AddLogging(b => b.AddSmplkit(client))`. The previous wrap-factory pattern (`adapter.Factory`) and the reflection-based `LoggingClient.AutoLoadAdapters` / `TryLoadAdapter` were removed — explicit DI wiring is now the only path. Serilog support is unchanged (its single-root model has no equivalent of an `ILoggerProvider` registry; explicit `GetOrCreateSwitch(name)` wired into `LoggerConfiguration` remains the documented idiom).
