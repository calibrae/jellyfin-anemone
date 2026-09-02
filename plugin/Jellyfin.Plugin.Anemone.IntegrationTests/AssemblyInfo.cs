// anemone: two independent reasons this whole assembly needs to run single-threaded, not per-class:
// 1. Jellyfin.Plugin.Anemone.TestKit.PluginInstanceScope installs a real Plugin instance as the static
//    Plugin.Instance for the duration of a harness (AnemoneIntegrationHarness always uses one) - see its
//    own remarks. Two tests racing to install/restore that process-wide static would be flaky in ways
//    that have nothing to do with the code under test.
// 2. AnemoneIntegrationHarness/FreePort.Find() pre-allocates an OS-assigned ephemeral port, then hands it
//    to a real Kestrel Listen() call moments later - a real (if narrow) TOCTOU window. Concurrent test
//    classes each doing this at once measurably increases the odds two of them race for the same port.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
