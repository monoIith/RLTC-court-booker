using RockcliffeCourtBooker.Automation;

namespace RockcliffeCourtBooker.Automation.Tests;

public sealed class PlaywrightRuntimePathTests
{
    [Fact]
    public void AppRootUsesOnlyBrowserDirectoryAtAppRoot()
    {
        var appRoot = Path.GetFullPath(Path.Combine("test-install", "Rockcliffe Court Booker"));

        var candidates = PlaywrightRuntime.ResolveBundledBrowserDirectories(appRoot);

        Assert.Equal([Path.Combine(appRoot, "ms-playwright")], candidates);
    }

    [Fact]
    public void WorkerDirectoryFallsBackToBrowserDirectoryAtAppRoot()
    {
        var appRoot = Path.GetFullPath(Path.Combine("test-install", "Rockcliffe Court Booker"));
        var workerDirectory = Path.Combine(appRoot, "worker");

        var candidates = PlaywrightRuntime.ResolveBundledBrowserDirectories(workerDirectory);

        Assert.Equal(
            [
                Path.Combine(workerDirectory, "ms-playwright"),
                Path.Combine(appRoot, "ms-playwright"),
            ],
            candidates);
    }

    [Fact]
    public void ArbitrarySubdirectoryDoesNotTrustBrowserDirectoryAtParent()
    {
        var appRoot = Path.GetFullPath(Path.Combine("test-install", "Rockcliffe Court Booker"));
        var arbitraryDirectory = Path.Combine(appRoot, "tools");

        var candidates = PlaywrightRuntime.ResolveBundledBrowserDirectories(arbitraryDirectory);

        Assert.Equal([Path.Combine(arbitraryDirectory, "ms-playwright")], candidates);
        Assert.DoesNotContain(Path.Combine(appRoot, "ms-playwright"), candidates);
    }
}
