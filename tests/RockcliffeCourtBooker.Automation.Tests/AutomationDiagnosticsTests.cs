using RockcliffeCourtBooker.Automation.Diagnostics;

namespace RockcliffeCourtBooker.Automation.Tests;

public sealed class AutomationDiagnosticsTests
{
    [Fact]
    public void RetentionDeletesOnlyKnownArtifactsInManagedAttemptDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rockcliffe-diagnostics-{Guid.NewGuid():N}");
        var managed = Path.Combine(root, $"20260101-010101-{Guid.NewGuid():D}");
        var unrelated = Path.Combine(root, "unrelated-user-folder");
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(managed, "failure.png"), "fixture");
        File.WriteAllText(Path.Combine(managed, "trace.zip"), "fixture");
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "fixture");
        Directory.SetLastWriteTimeUtc(managed, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));

        try
        {
            AutomationDiagnostics.DeleteExpiredArtifacts(root, TimeSpan.FromDays(14));

            Assert.False(Directory.Exists(managed));
            Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RetentionLeavesUnexpectedFilesAndDoesNotRecursivelyDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rockcliffe-diagnostics-{Guid.NewGuid():N}");
        var managed = Path.Combine(root, $"20260101-010101-{Guid.NewGuid():D}");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "failure.png"), "fixture");
        File.WriteAllText(Path.Combine(managed, "unexpected.txt"), "fixture");
        Directory.SetLastWriteTimeUtc(managed, DateTime.UtcNow.AddDays(-30));

        try
        {
            AutomationDiagnostics.DeleteExpiredArtifacts(root, TimeSpan.FromDays(14));

            Assert.True(Directory.Exists(managed));
            Assert.False(File.Exists(Path.Combine(managed, "failure.png")));
            Assert.True(File.Exists(Path.Combine(managed, "unexpected.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
