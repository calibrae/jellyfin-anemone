# Jellyfin Plugin Interception Research: Remote FFmpeg Offload

**Repos cloned (shallow, `master`):**
- `jellyfin/jellyfin` @ `6ad1e341b18432a7c7309cbd3f744cf6c2cb5ffe`, cloned 2026-08-28. `SharedVersion.cs` -> **12.0.0**. `global.json` -> .NET SDK **10.0.0**. `MediaBrowser.Controller.csproj` -> target `net10.0`.
- `jellyfin/jellyfin-plugin-template` @ HEAD (shallow clone, same date). Its `.csproj` pins `<PackageReference Include="Jellyfin.Controller" Version="10.9.11">` and `<TargetFramework>net9.0</TargetFramework>` - **the template is stale relative to the `master` server code** (10.9.11 vs. 12.0.0-dev). Treat template-derived claims as "current pattern, but re-check exact API surface against the 12.0.0 `Jellyfin.Controller` package before coding."

All paths below are relative to the clone root:
`/private/tmp/claude-501/-Users-cali-Developer-perso-jellyfincluster/1e25599c-daf3-414d-9fd5-92ce927b36cc/scratchpad/agent-plugin/jellyfin/` and `.../jellyfin-plugin-template/`.

---

## 1. Plugin DI registration order - does a plugin's `IMediaEncoder`/`ITranscodeManager` win?

**Yes, plugin registrations win**, because they are appended to the same `IServiceCollection` after core registrations, and the actual `ServiceProvider` isn't built until every registration has been collected.

Call chain, in order:

1. `Jellyfin.Server/Program.cs:169-171`:
   ```csharp
   _jellyfinHost = Host.CreateDefaultBuilder()
       .UseConsoleLifetime()
       .ConfigureServices(services => appHost.Init(services))
       ...
       .Build();   // <-- ServiceProvider is built HERE, after all ConfigureServices delegates ran
   ```
2. `Emby.Server.Implementations/ApplicationHost.cs:462-492` (`Init`):
   ```csharp
   public void Init(IServiceCollection serviceCollection)
   {
       DiscoverTypes();                          // line 464 - loads plugin assemblies + reflects all types
       ...
       RegisterServices(serviceCollection);       // line 490 - virtual, core registrations
       _pluginManager.RegisterServices(serviceCollection);  // line 492 - plugin registrations, AFTER core
   }
   ```
3. `RegisterServices` is virtual; `Jellyfin.Server/CoreAppHost.cs:63-112` overrides it, registers Jellyfin.Server-specific stuff, then explicitly calls `base.RegisterServices(serviceCollection)` at **line 111**, which is `ApplicationHost.RegisterServices` (`ApplicationHost.cs:499-638`). That base method registers, among others:
   - `ApplicationHost.cs:567` - `serviceCollection.AddSingleton<IMediaEncoder, MediaBrowser.MediaEncoding.Encoder.MediaEncoder>();`
   - `ApplicationHost.cs:630` - `serviceCollection.AddSingleton<ITranscodeManager, TranscodeManager>();`
4. Only after all of the above does `PluginManager.RegisterServices` run (`Emby.Server.Implementations/Plugins/PluginManager.cs:206-232`):
   ```csharp
   /// Registers the plugin's services with the DI.
   /// Note: DI is not yet instantiated yet.
   public void RegisterServices(IServiceCollection serviceCollection)
   {
       foreach (var pluginServiceRegistrator in _appHost.GetExportTypes<IPluginServiceRegistrator>())
       {
           ...
           var instance = (IPluginServiceRegistrator?)Activator.CreateInstance(pluginServiceRegistrator);
           instance?.RegisterServices(serviceCollection, _appHost);   // line 226
       }
   }
   ```
   The comment on line 205 ("DI is not yet instantiated yet") is the codebase's own confirmation that this all happens pre-`Build()`.

Because .NET's `Microsoft.Extensions.DependencyInjection` container resolves a *single* dependency (constructor parameter, or `GetService<T>()`/`Resolve<T>()`) to the **last** registration for that service type - multiple registrations only matter for `IEnumerable<T>` resolution - a plugin that does:

```csharp
public class MyRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
        => services.AddSingleton<IMediaEncoder, RemoteMediaEncoder>();
}
```

will have `RemoteMediaEncoder` win for every constructor-injected `IMediaEncoder` and every `Resolve<IMediaEncoder>()`/`GetService<IMediaEncoder>()` call, everywhere in the app, including inside `TranscodeManager` itself (which takes `IMediaEncoder` via DI - see `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs`, field `_mediaEncoder`, used at e.g. line 394 `_mediaEncoder.EncoderPath`).

**No bypass via concrete-type resolution.** Grepped the whole tree for `GetRequiredService<MediaEncoder>`, `GetService<MediaEncoder>`, `Resolve<MediaEncoder>`, `typeof(MediaEncoder)` - zero hits outside `IMediaEncoder`. Every one of the 34 consumer files (`grep -rl "IMediaEncoder "`) injects the interface, e.g. `MediaBrowser.Controller/Entities/TV/Episode.cs:27` - `public static IMediaEncoder MediaEncoder { get; set; }` - is set via `Episode.MediaEncoder = Resolve<IMediaEncoder>();` in `ApplicationHost.cs:712`, itself only reached from `SetStaticProperties()` -> `InitializeServices()` (`ApplicationHost.cs:645-652`), which Program.cs calls (line 215, `await appHost.InitializeServices(...)`) **after** `appHost.ServiceProvider = _jellyfinHost.Services` (Program.cs:195), i.e. after the fully-merged container (including plugin registrations) is built. Same applies to plugin `IPlugin` instance construction itself (`FindParts()` -> `CreatePlugins()` at `ApplicationHost.cs:652,728`, using `ActivatorUtilities.CreateInstance(ServiceProvider, ...)` at `ApplicationHost.cs:335` - `ServiceProvider` is non-null by that point).

Order summary (all inside the single `ConfigureServices` callback before `.Build()`):
`DiscoverTypes()` (loads plugin DLLs + reflects types) -> `CoreAppHost.RegisterServices` (Jellyfin.Server-specific) -> `base.RegisterServices` = `ApplicationHost.RegisterServices` (registers `IMediaEncoder`, `ITranscodeManager`, everything else core) -> `PluginManager.RegisterServices` (plugin `IPluginServiceRegistrator`s, **last, so they win**).

---

## 2. Is `IMediaEncoder`/`ITranscodeManager` public, stable, and decorator-friendly?

**Yes on public/packaged. Yes on decorator-friendly, with a caveat on size.**

- Both interfaces live in `MediaBrowser.Controller/MediaEncoding/IMediaEncoder.cs` and `MediaBrowser.Controller/MediaEncoding/ITranscodeManager.cs`, i.e. inside the `MediaBrowser.Controller` project.
- That project's `.csproj` (`MediaBrowser.Controller/MediaBrowser.Controller.csproj:10-11`) declares `<PackageId>Jellyfin.Controller</PackageId>` `<VersionPrefix>12.0.0</VersionPrefix>` - **this is exactly the NuGet package the plugin template references** (`jellyfin-plugin-template/Jellyfin.Plugin.Template/Jellyfin.Plugin.Template.csproj:14`, pinned there at the older `10.9.11`). `MediaBrowser.Common` is a separate project reference (`MediaBrowser.Controller.csproj:28`) packaged as `Jellyfin.Common` (`MediaBrowser.Common/MediaBrowser.Common.csproj:10`) and pulled in transitively.

- **`IMediaEncoder`** (`MediaBrowser.Controller/MediaEncoding/IMediaEncoder.cs:22-284`): extends `ITranscoderSupport` (3 members: `CanEncodeToAudioCodec`, `CanEncodeToSubtitleCodec`, `CanExtractSubtitles` - `MediaBrowser.Model/Dlna/ITranscoderSupport.cs`) plus **24 of its own members**: `EncoderPath`, `ProbePath`, `EncoderVersion`, `IsPkeyPauseSupported`, 5x `IsVaapiDevice*`/`IsVideoToolboxAv1DecodeAvailable` capability flags, `SupportsEncoder/Decoder/Hwaccel/Filter/FilterWithOption/BitStreamFilterWithOption`, `ExtractAudioImage`, 2 overloads of `ExtractVideoImage`, `ExtractVideoImagesOnIntervalAccelerated`, `GetMediaInfo`, 2 overloads of `GetInputArgument`, `GetExternalSubtitleInputArgument`, `GetTimeParameter`, `ConvertImage`, `EscapeSubtitleFilterPath`, `SetFFmpegPath`, `GetPrimaryPlaylistVobFiles/M2tsFiles`, 2 overloads of `GetInputPathArgument`, `GenerateConcatConfig`. **27 members total** - a plugin implementing this interface directly from scratch is a large undertaking (probing, image extraction, HW-capability introspection, argument-string helpers - none of which relate to "run ffmpeg remotely").

- **`ITranscodeManager`** (`MediaBrowser.Controller/MediaEncoding/ITranscodeManager.cs:11-105`) is far smaller and much more precisely targeted at the actual use case: `GetTranscodingJob` (x2), `PingTranscodingJob`, `KillTranscodingJobs`, `ReportTranscodingProgress`, **`StartFfMpeg(StreamState state, string outputPath, string commandLineArguments, Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource, string? workingDirectory = null)`** (line 75-82), `OnTranscodeBeginRequest`, `OnTranscodeEndRequest`, `LockAsync`. **10 members.** `StartFfMpeg` receives the fully-built ffmpeg command line as a plain string and an `outputPath`, and returns a `TranscodingJob` - this is the natural interception seam for "ship this job to a remote agent instead of running ffmpeg locally."

- **Decorator pattern is viable for both**, and is the recommended approach rather than reimplementing the whole interface: register the plugin's wrapper as `IMediaEncoder`/`ITranscodeManager`, and internally hold a reference to a real instance to delegate the 90% of members you don't want to change.
  - `TranscodeManager` (concrete class, `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs:34`) is declared `public sealed class TranscodeManager : ITranscodeManager, IDisposable` - sealed, so no subclassing, but that's irrelevant to composition-based decoration.
  - `MediaEncoder` (concrete class, `MediaBrowser.MediaEncoding/Encoder/MediaEncoder.cs:43`) is `public partial class MediaEncoder : IMediaEncoder, IDisposable` - **not sealed**, but not registered as itself in DI either way (core only does `AddSingleton<IMediaEncoder, MediaEncoder>()`, never `AddSingleton<MediaEncoder>()`).
  - To decorate, a plugin's `RegisterServices` needs to *additionally* register the concrete type so it can inject the "real" instance into the wrapper, e.g.:
    ```csharp
    services.AddSingleton<TranscodeManager>();           // register concrete, still resolvable by itself
    services.AddSingleton<ITranscodeManager>(sp =>
        new RemoteAwareTranscodeManager(sp.GetRequiredService<TranscodeManager>(), ...));
    ```
    This is safe because nothing else in core resolves the concrete `TranscodeManager`/`MediaEncoder` types directly (confirmed by the grep in section 1) - only the interfaces are ever requested, so having two DI entries (concrete + decorated interface) causes no double-registration conflict.
  - `TranscodingJob` (`MediaBrowser.Controller/MediaEncoding/TranscodingJob.cs:12,63`) is a public `sealed class TranscodingJob : IDisposable` with a public constructor `TranscodingJob(ILogger<TranscodingJob> logger)` (line 24) and a **nullable** `public Process? Process { get; set; }` (line 63) - a plugin's decorator can legally construct a `TranscodingJob` with `Process = null` to represent a job that's actually running on a remote agent, as long as it's careful about any code path elsewhere that dereferences `.Process` unconditionally (not audited exhaustively - see "not verified" section).

---

## 3. Process-launching abstraction

**None exists.** There is no `IProcessFactory` or any process-launching abstraction anywhere in this codebase (that pattern is an old-Emby thing; not present in current Jellyfin). Grep for `IProcessFactory`/`ProcessFactory` across the whole repo: zero hits.

Every ffmpeg/ffprobe invocation instantiates `new Process { StartInfo = new ProcessStartInfo { ... } }` directly, in-line, in the class that needs it:
- `MediaBrowser.MediaEncoding/Encoder/MediaEncoder.cs:524,783,1034` (ffprobe / image extraction / interval extraction) + `MediaBrowser.MediaEncoding/Encoder/EncoderValidator.cs:640,680` (capability probing).
- **`MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs:417-434`** - the actual live-transcode launch (inside `StartFfMpeg`), `FileName = _mediaEncoder.EncoderPath`, `Arguments = commandLineArguments`.
- `MediaBrowser.MediaEncoding/Subtitles/SubtitleEncoder.cs:870`, `MediaBrowser.MediaEncoding/Attachments/AttachmentExtractor.cs:184,298,432`, `Emby.Server.Implementations/ScheduledTasks/Tasks/AudioNormalizationTask.cs:232`, `src/Jellyfin.LiveTv/Recordings/RecordingsManager.cs:813-814`, `src/Jellyfin.LiveTv/IO/EncodedRecorder.cs:81,108`, `src/Jellyfin.MediaEncoding.Keyframes/FfProbe/FfProbeKeyframeExtractor.cs:23`.

Consequence for the project: there is **no single choke point at the OS-process layer**. Interception has to happen one level up, at the interface boundary (`ITranscodeManager`/`IMediaEncoder`), by *replacing the whole method* that would otherwise call `new Process(...)`, not by hooking into how the process gets started.

---

## 4. Plugin HTTP endpoints: ApplicationPart discovery, auth policies, upload size limits

**Automatic controller discovery, no registration boilerplate needed.**

- `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:111-162` (`AddJellyfinApi`) does:
  ```csharp
  .ConfigureApplicationPartManager(a => a.ApplicationParts.Clear())   // line 135
  .AddApplicationPart(typeof(StartupController).Assembly)             // line 136
  ...
  foreach (Assembly pluginAssembly in pluginAssemblies)                // line 159
  {
      mvcBuilder.AddApplicationPart(pluginAssembly);                   // line 160
  }
  ```
- Called from `Jellyfin.Server/Startup.cs:72`: `services.AddJellyfinApi(_serverApplicationHost.GetApiPluginAssemblies(), ...)`.
- `GetApiPluginAssemblies()` (`Emby.Server.Implementations/ApplicationHost.cs:1010-1022`):
  ```csharp
  var assemblies = _allConcreteTypes
      .Where(i => typeof(ControllerBase).IsAssignableFrom(i))
      .Select(i => i.Assembly)
      .Distinct();
  ```
  `_allConcreteTypes` is populated by `DiscoverTypes()` from `GetComposablePartAssemblies()` (`ApplicationHost.cs:881-931`), which yields plugin assemblies **first** (`foreach (var p in _pluginManager.LoadAssemblies()) yield return p;` at line 883-886). **So: a plugin just needs a public `class MyController : ControllerBase` (with routing attributes) anywhere in its assembly; Jellyfin will auto-discover and wire it up** - no manual `AddApplicationPart` call needed on the plugin's side.

**Auth policies available for `[Authorize(Policy = Policies.X)]`** (`MediaBrowser.Common/Api/Policies.cs:1-97`, constants; registered in `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:63-88`): `FirstTimeSetupOrElevated`, `RequiresElevation`, `LocalAccessOnly`, `IgnoreParentalControl`, `Download`, `FirstTimeSetupOrDefault`, `LocalAccessOrRequiresElevation`, `AnonymousLanAccessPolicy`, `FirstTimeSetupOrIgnoreParentalControl`, `SyncPlayHasAccess/CreateGroup/JoinGroup/IsInGroup`, `CollectionManagement`, `LiveTvAccess/Management`, `SubtitleManagement`, `LyricManagement`. Example usage pattern from core: `Jellyfin.Api/Controllers/ApiKeyController.cs:37,53,69` - `[Authorize(Policy = Policies.RequiresElevation)]`. A plugin controller can use the same attribute with the same constants (via the `Jellyfin.Controller`/`Jellyfin.Common` packages) or define a custom `IAuthorizationHandler`/policy of its own via its `IPluginServiceRegistrator`.

**Upload size limits: not overridden globally, so the ASP.NET Core/Kestrel default applies.** Searched `Jellyfin.Server`/`Emby.Server.Implementations` for `MaxRequestBodySize`, `RequestSizeLimit`, `Limits.` overrides - the only Kestrel config is `Jellyfin.Server/Extensions/WebHostBuilderExtensions.cs:69-135` (`SetupJellyfinWebServer`), which only calls `options.Listen(...)`/`options.ListenUnixSocket(...)` for bind addresses/certs - it never touches `options.Limits.MaxRequestBodySize`. The only place in the whole codebase using `[RequestSizeLimit]` is `Jellyfin.Api/Controllers/ClientLogController.cs:50` (for client log uploads). This means Kestrel's built-in default (30,000,000 bytes ~= 28.6 MiB per request - an ASP.NET Core framework default, not Jellyfin-authored code) applies to any plugin endpoint unless the plugin action is decorated with `[RequestSizeLimit(long.MaxValue)]` / `[DisableRequestSizeLimit]` (standard `Microsoft.AspNetCore.Mvc` attributes, no Jellyfin-specific gating) - or the plugin reads the request body as a stream (`Request.Body`) instead of a buffered model-bound parameter, which is the standard way to accept large binary PUT/POST bodies (e.g. segment uploads) without buffering the whole thing in memory.

---

## 5. What a plugin can get from the host: temp path, EncodingOptions, base URL, API keys

- **Transcode temp path**: `MediaBrowser.Common/Configuration/EncodingConfigurationExtensions.cs:29-40` - `IConfigurationManager.GetTranscodePath()` extension method, falls back to `CachePath/transcodes`, creates the dir. `IConfigurationManager` is available via DI (`AddSingleton<IConfigurationManager>` in `ApplicationHost.cs:503`).
- **EncodingOptions (incl. ffmpeg path)**: `EncodingConfigurationExtensions.cs:17-18` - `IConfigurationManager.GetEncodingOptions()` -> `EncodingOptions` (`MediaBrowser.Model/Configuration/EncodingOptions.cs`), which has `EncoderAppPath`/`EncoderAppPathDisplay` (lines 136,141) - the on-disk ffmpeg path the admin configured. Both extension methods live in `MediaBrowser.Common` -> packaged as `Jellyfin.Common`, transitively available via the `Jellyfin.Controller` package dependency.
- **Server base URL**: `MediaBrowser.Controller/IServerApplicationHost.cs:51-90` exposes `GetSmartApiUrl(HttpRequest)`, `GetSmartApiUrl(IPAddress)`, `GetSmartApiUrl(string hostname)`, `GetApiUrlForLocalAccess(IPAddress?, bool allowHttps = true)` (line 73), `GetLocalApiUrl(string hostname, string? scheme, int? port)` (line 89). `IServerApplicationHost` is itself injected into `IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost applicationHost)` (`MediaBrowser.Controller/Plugins/IPluginServiceRegistrator.cs:18`) and is also DI-resolvable anywhere (`AddSingleton<IServerApplicationHost>(this)`, `ApplicationHost.cs:596`).
- **API key minting**: `MediaBrowser.Controller/Security/IAuthenticationManager.cs:16` - `Task CreateApiKey(string name)`. Concrete impl `Jellyfin.Server.Implementations/Security/AuthenticationManager.cs:26-35`:
  ```csharp
  public async Task CreateApiKey(string name)
  {
      var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
      await using (dbContext.ConfigureAwait(false))
      {
          dbContext.ApiKeys.Add(new ApiKey(name));      // token generated inside the ApiKey entity ctor
          await dbContext.SaveChangesAsync().ConfigureAwait(false);
      }
  }
  ```
  Note `CreateApiKey` returns `Task`, not `Task<string>` - **it does not hand back the newly minted token**. To retrieve it, call `GetApiKeys()` (line 38-54) afterward and find the entry matching the `name` you passed (there's a small TOCTOU risk if two keys with the same name get created concurrently - not something core guards against). `IAuthenticationManager` is registered `AddScoped` (`CoreAppHost.cs:99` - `serviceCollection.AddScoped<IAuthenticationManager, AuthenticationManager>();`), so outside of an HTTP request (e.g. from a plugin's `IHostedService`) you must create a DI scope via `IServiceScopeFactory` to resolve it - you cannot inject it directly into a singleton. `Jellyfin.Api/Controllers/ApiKeyController.cs:37-58` is the reference implementation (`[Authorize(Policy = Policies.RequiresElevation)]`, `POST /Auth/Keys?app=...`) - a plugin could either call this HTTP endpoint itself (with an already-elevated session) or replicate the DI-injection pattern directly against `IAuthenticationManager`.

---

## 6. Plugin hosting facts

- **Target framework**: server (`master`) is **.NET 10** (`global.json` -> SDK `10.0.0`; `MediaBrowser.Controller.csproj:37` -> `net10.0`). The plugin template as cloned still targets **`net9.0`** and pins `Jellyfin.Controller 10.9.11` (`jellyfin-plugin-template/Jellyfin.Plugin.Template/Jellyfin.Plugin.Template.csproj:4,14,17`) - this is a version-skew warning: build against the actual target server's `Jellyfin.Controller`/`Jellyfin.Model` NuGet version and TFM, not blindly off the template as cloned.
- **Packaging**: per-plugin `meta.json` (constant `MetafileName = "meta.json"`, `Emby.Server.Implementations/Plugins/PluginManager.cs:34`), deserialized into `PluginManifest` (`MediaBrowser.Common/Plugins/PluginManifest.cs:11-125`): `guid`(Id), `name`, `version`, `targetAbi`, `category`, `changelog`, `description`, `overview`, `owner`, `timestamp`, `status`, `autoUpdate`, `imagePath`/`imageResourceName`, `assemblies` (list of DLL paths relative to the plugin folder, line 123-124).
- **Installation**: two supported paths.
  1. **Repository install** via dashboard: `Emby.Server.Implementations/Updates/InstallationManager.cs:102` (`GetPackages(string manifestName, string manifestUrl, ...)`) fetches a repo-level `manifest.json` listing `PackageInfo[]` (with `VersionInfo`s carrying `RepositoryUrl`, set at line 122), downloaded and unpacked into the plugin folder.
  2. **Manual drop**: `LoadManifest(string dir)` (`PluginManager.cs:677-741`) works even with **no** `meta.json` present - if the metafile is missing it auto-synthesizes a `PluginManifest` from the folder name (and a trailing `_<version>` suffix if present, line 715-726) and marks it `Active`. So dropping a raw DLL folder into `plugins/<name>/` is a legitimate, supported path, not just an internal fallback.
  3. `DiscoverPlugins()` (`PluginManager.cs:745-765`) simply enumerates all top-level subdirectories of the configured plugins path and calls `LoadManifest` on each.
- **Config page mechanics**: `IHasWebPages.GetPages()` (`MediaBrowser.Model/Plugins/IHasWebPages.cs`) returns `PluginPageInfo` objects (`MediaBrowser.Model/Plugins/PluginPageInfo.cs`) with an `EmbeddedResourcePath` pointing at an embedded resource inside the plugin's own assembly (template: `.csproj:29-30` - `<EmbeddedResource Include="Configuration\configPage.html" />`, referenced in `Plugin.cs:47` as `"{Namespace}.Configuration.configPage.html"`). So: config UI = a raw HTML(+inline JS) file embedded in the plugin DLL, served by the dashboard shell; there is no separate build step or bundler required - it's a static resource string match.
- **Config persistence**: `BasePlugin<TConfigurationType>` (`MediaBrowser.Common/Plugins/BasePluginOfT.cs:18-199`), constrained to `where TConfigurationType : BasePluginConfiguration`. Config is lazily loaded (`Configuration` getter, lines 102-119) via `IXmlSerializer.DeserializeFromFile` from `ApplicationPaths.PluginConfigurationsPath/<AssemblyFileName>.xml` (`ConfigurationFilePath`, line 131), and saved via `XmlSerializer.SerializeToFile` (`SaveConfiguration`, lines 143-152) - plain XML serialization, no encryption, no schema versioning beyond whatever the plugin author adds. `UpdateConfiguration(BasePluginConfiguration)` (lines 163-172) is what the dashboard's save button calls; it fires `ConfigurationChanged` afterward.

---

## 7. Plugin startup code: current supported mechanism

`IServerEntryPoint` **does not exist anywhere in this codebase** (`grep -rn "IServerEntryPoint"` across the whole tree: zero hits, in both the server repo and the plugin template) - confirms it's gone from current Jellyfin (it was the old mechanism).

**Current mechanism: standard ASP.NET Core `IHostedService`, registered by the plugin itself inside `IPluginServiceRegistrator.RegisterServices`.** Core code does exactly this pattern itself - `Jellyfin.Server/Startup.cs:152-157`:
```csharp
services.AddHostedService<RecordingsHost>();
services.AddHostedService<AutoDiscoveryHost>();
services.AddHostedService<NfoUserDataSaver>();
services.AddHostedService<LibraryChangedNotifier>();
services.AddHostedService<UserDataChangeNotifier>();
services.AddHostedService<RecordingNotifier>();
```
with implementations like `Jellyfin.Server.Implementations/Users/DeviceAccessHost.cs:18` (`public sealed class DeviceAccessHost : IHostedService`) and `src/Jellyfin.LiveTv/Recordings/RecordingsHost.cs:12`. Because the whole server is built on the .NET Generic Host (`Host.CreateDefaultBuilder()`, `Program.cs:169`), any `IHostedService` registered via `services.AddHostedService<T>()` - including from a plugin's `IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)` - gets `StartAsync`/`StopAsync` called automatically by the host lifecycle. This is the recommended way to run background/startup code (e.g., spinning up the plugin's own remote-agent coordination loop, opening a listen socket for agent callbacks, etc.).

---

## 8. Existing plugins/prior art wrapping or replacing the media encoder

- **`joshuaboniface/rffmpeg`** (https://github.com/joshuaboniface/rffmpeg) - the dominant real-world solution today. It is **not a Jellyfin plugin**; it's a standalone wrapper binary that you point Jellyfin's `EncoderAppPath` (ffmpeg path setting) at, which then SSHes to a remote host to actually run ffmpeg/ffprobe and streams the result back to the local expected output path. This is exactly the "wrapper binary configured as the ffmpeg path" fallback the task asked me to weigh against a real plugin-based interception.
- **`JacquesToT/Transcodarr`** (https://github.com/JacquesToT/Transcodarr) - similar space: offloads live transcoding to remote (Apple Silicon/VideoToolbox) nodes; also external-tool-based rather than an in-process Jellyfin plugin.
- **`NathanBland/jellyfin-plugins` -> `recording-transcoder`** - a real plugin that *consumes* `IMediaEncoder` (reads `EncoderPath`/`ProbePath` off the injected service) but does not replace/override it.
- **`jellyfin/jellyfin-plugin-transcodekiller`, `mugurc/jellyfin-plugin-pre-transcode`, `6Leoo6/jellyfin-precode`, `lucapolesel/BlockTranscoding`** - plugins that interact with the transcoding subsystem (killing stuck jobs, pre-transcoding libraries ahead of time, blocking transcodes by resolution) via the standard `IMediaEncoder`/library APIs, none of them replace the encoder/transcode-manager implementation itself.
- **`jellyfin/jellyfin-meta` Discussion #36 - "[Proposal] FFmpeg call handing refactoring and FFmpeg remote integration"** (https://github.com/jellyfin/jellyfin-meta/discussions/36) - a Jellyfin-team design discussion floating an in-core "JellyfinRemoteFFmpegServer (JRFS)" concept for serializing ffmpeg calls to run remotely, explicitly acknowledged in the discussion itself as under-specified/unshipped. Confirms: **no official first-class remote-ffmpeg abstraction exists in Jellyfin today** - this would be net-new plugin-side engineering either way, and the DI-override path found in section 1 is not an officially documented "supported extension point" for this purpose, just an emergent consequence of how the DI container is wired.

I did not find any existing plugin that overrides `IMediaEncoder`/`ITranscodeManager` via DI the way this project would need to - the DI-override technique described in section 1 appears to be unused in practice; every real offload solution I found uses the wrapper-binary approach instead.

---

## VERDICT

- **Can a plugin override `IMediaEncoder`? Yes.** Registering `services.AddSingleton<IMediaEncoder, YourImpl>()` inside `IPluginServiceRegistrator.RegisterServices` wins over the core registration, because plugin service registration (`PluginManager.RegisterServices`, called at `ApplicationHost.cs:492`) runs strictly after core registration (`ApplicationHost.cs:490`) and the DI container isn't built until after both have run (`Program.cs:185`, `.Build()`). Confirmed no code resolves the concrete `MediaEncoder` type in a way that would bypass this.
- **Can a plugin override `ITranscodeManager`? Yes, same mechanism, and it's the better interception point.** `ITranscodeManager` is 10 members vs. `IMediaEncoder`'s 27, and its `StartFfMpeg(state, outputPath, commandLineArguments, ...)` method is the exact seam where "launch ffmpeg locally" happens - everything upstream (building `commandLineArguments` via `EncodingHelper`, HLS/DASH playlist logic, session/state tracking) is untouched, so a decorator only needs to reimplement one method plus pass-through the rest to a wrapped real `TranscodeManager`.
- **Recommended interception point: decorate `ITranscodeManager`, specifically `StartFfMpeg`.** Register the concrete `TranscodeManager`/`MediaEncoder` types additionally (`services.AddSingleton<TranscodeManager>()`) so the plugin's decorator can hold a real instance for delegation of everything it doesn't want to change (progress reporting, job bookkeeping, `PingTranscodingJob`, `LockAsync`, etc.), and only intercept `StartFfMpeg` to ship the job to a remote agent instead of spawning `new Process()` locally. Return a `TranscodingJob` (public ctor, `Process` nullable) representing the remote job. Complement with an `IMediaEncoder` decorator only if you also want to offload probing/thumbnail/keyframe extraction (`GetMediaInfo`, `ExtractVideoImage*`) - those are separate, smaller pieces of work you can choose to keep local if desired.
- **No process-launching abstraction exists** (`IProcessFactory` or similar) - not needed anyway, since the plugin approach intercepts one level above the process launch.
- **HTTP surface for the remote agent to talk back to Jellyfin is straightforward**: drop a `ControllerBase` in the plugin assembly, it's auto-discovered (`GetApiPluginAssemblies`); use `[Authorize(Policy = Policies.X)]` with the existing policy set or define a custom one; **must** add `[DisableRequestSizeLimit]`/`[RequestSizeLimit(...)]` to any action receiving large binary segment uploads, since Jellyfin does not raise Kestrel's default request-body cap globally.
- **All the host facts a plugin needs are available and public**: transcode temp path and ffmpeg path via `IConfigurationManager.GetTranscodePath()`/`GetEncodingOptions()`, server base URL via `IServerApplicationHost.GetApiUrlForLocalAccess`/`GetLocalApiUrl`/`GetSmartApiUrl`, and API keys can be minted programmatically via `IAuthenticationManager.CreateApiKey(name)` (though the token itself must be fetched back via `GetApiKeys()`, and the service is `Scoped` so a background service needs `IServiceScopeFactory`).
- **This is genuinely feasible as a real plugin, not just a wrapper-binary hack** - but it rides on an emergent property of the DI container ordering rather than a documented/officially-supported "override the encoder" extension point. No production plugin does this today; every real-world remote-transcode solution found (`rffmpeg`, `Transcodarr`) uses the wrapper-binary approach instead, likely for exactly that reason: it's simpler, and doesn't need to track Jellyfin's internal `ITranscodeManager`/`IMediaEncoder` interface shape across versions. **Recommendation: the plugin/DI-override path is worth building for a tighter, more Jellyfin-native integration (session awareness, native config UI, no SSH/wrapper-binary indirection), but budget for interface churn across Jellyfin releases** (both interfaces are in the actively-developed `MediaBrowser.Controller`/`Jellyfin.Controller` package, and - per the repo note above - the plugin template itself still lags the server by several minor versions, suggesting the ecosystem doesn't guarantee tight compatibility windows).

---

## What I did NOT verify

- Did **not** build or run either repo (no `dotnet build`/`dotnet test` executed) - all claims are static-read from source, not confirmed by compiling or exercising the DI container at runtime.
- Did **not** exhaustively audit every code path that reads `TranscodingJob.Process` to confirm none of them NPE on a null `Process` (e.g. process-priority tweaks, `HasExited` checks, kill logic in `TranscodingJob.cs` around lines 268-297) - a real decorator implementation would need to trace `SessionManager`/`TranscodingJobHelper`-equivalent consumers of `TranscodingJob.Process` more thoroughly than done here.
- Did **not** check every method in `MediaEncoder`/`TranscodeManager` for `virtual`/override-ability - irrelevant to the recommended decorator-via-composition approach, but means inheritance-based partial overrides were not evaluated as an alternative.
- Did **not** verify the exact default value of Kestrel's `MaxRequestBodySize` (30,000,000 bytes) against Jellyfin's specific ASP.NET Core version pin - stated from general ASP.NET Core/Kestrel framework knowledge, not from a line of Jellyfin source (confirmed only that Jellyfin doesn't override it).
- Did **not** check plugin-repository JSON schema (`manifest.json` at the repo level, as opposed to per-plugin `meta.json`) in full detail - read only enough of `InstallationManager.cs` to confirm the two-tier repo/package model exists.
- Did **not** review `EncodingHelper` (the class that actually builds the `commandLineArguments` string consumed by `ITranscodeManager.StartFfMpeg`) in depth - relevant if the remote agent needs to understand/rewrite that command line (e.g., swap hardware-acceleration flags for the remote machine's GPU) rather than treat it as an opaque string.
- Did **not** check how `SessionManager`/playback-reporting code queries `TranscodingJob` state (bitrate, position) during an active stream - needed to design how a remote-agent-backed job reports progress without a real local `Process` object to introspect.
- Web search results (Q8) were not independently re-verified by opening each linked plugin's actual source - summarized from search snippets/DeepWiki description text, not from cloning those repos.
- Did not confirm whether `LocalAccessOnly` (present in `Policies.cs:21` but not spotted in the `AddPolicy` calls I read in `ApiServiceCollectionExtensions.cs:63-88`) is registered via a different code path - flagged but not chased down; not relevant to the core question.
