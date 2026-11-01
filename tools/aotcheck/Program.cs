using System.Runtime.CompilerServices;
using ManiaReplayMaster.AotCheck;
using ManiaReplayMaster.TestSupport;

if (RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Publish this smoke test with Native AOT before running it.");
RepoPaths.Root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
IntegrationChecks.Run();
