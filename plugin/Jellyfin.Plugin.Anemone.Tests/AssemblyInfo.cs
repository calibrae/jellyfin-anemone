using Xunit;

// anemone: Jellyfin.Plugin.Anemone.TestKit.PluginInstanceScope installs a real Plugin instance as the
// static Plugin.Instance for the duration of a test (AnemoneTranscodeManagerHarness always uses one) -
// see its own remarks for why that's required. Plugin.Instance is process-wide state with no DI seam,
// so two tests racing to install/restore it concurrently would be flaky in ways that have nothing to do
// with the code under test. xUnit parallelizes across test classes by default; turn that off for this
// whole assembly rather than trying to scope it to just the tests that use PluginInstanceScope.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
