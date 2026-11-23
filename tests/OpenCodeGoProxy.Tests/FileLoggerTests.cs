using Microsoft.Extensions.Logging;
using OpenCodeGoProxy.Logging;
using Xunit;

namespace OpenCodeGoProxy.Tests;

public class FileLoggerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private const string FileName = "proxy-20260102.log";

    [Fact]
    public void Write_IncludesLevelCategoryAndMessage()
    {
        var directory = TempDirectory.Create();
        try
        {
            using var provider = new FileLoggerProvider(directory, clock: () => FixedNow);
            provider.CreateLogger("OpenCodeGoProxy.Tests.Category").LogInformation("hello {Name}", "world");

            var text = ReadAllTextShared(Path.Combine(directory, FileName));
            Assert.Contains("Information", text, StringComparison.Ordinal);
            Assert.Contains("OpenCodeGoProxy.Tests.Category", text, StringComparison.Ordinal);
            Assert.Contains("hello world", text, StringComparison.Ordinal);
            Assert.Contains("2026-01-02 03:04:05.000 Z", text, StringComparison.Ordinal);
        }
        finally
        {
            TempDirectory.TryDelete(directory);
        }
    }

    [Fact]
    public void Constructor_CreatesDirectoryAndWritesExceptionDetails()
    {
        var root = TempDirectory.Create();
        var directory = Path.Combine(root, "nested", "logs");
        try
        {
            using var provider = new FileLoggerProvider(directory, clock: () => FixedNow);
            Assert.True(Directory.Exists(directory));

            provider.CreateLogger("Cat").LogError(new InvalidOperationException("boom"), "failed");
            var text = ReadAllTextShared(Path.Combine(directory, FileName));
            Assert.Contains("failed", text, StringComparison.Ordinal);
            Assert.Contains("    System.InvalidOperationException: boom", text, StringComparison.Ordinal);
        }
        finally
        {
            TempDirectory.TryDelete(root);
        }
    }

    [Fact]
    public void SizeLimit_RollsTheCurrentFile()
    {
        var directory = TempDirectory.Create();
        try
        {
            using var provider = new FileLoggerProvider(directory, maxFileBytes: 150, clock: () => FixedNow);
            var logger = provider.CreateLogger("Cat");
            logger.LogInformation(new string('a', 100));
            logger.LogInformation(new string('b', 100));

            Assert.True(File.Exists(Path.Combine(directory, FileName)));
            Assert.True(File.Exists(Path.Combine(directory, "proxy-20260102.1.log")));

            var rolled = ReadAllTextShared(Path.Combine(directory, "proxy-20260102.1.log"));
            var current = ReadAllTextShared(Path.Combine(directory, FileName));
            Assert.Contains(new string('a', 100), rolled, StringComparison.Ordinal);
            Assert.Contains(new string('b', 100), current, StringComparison.Ordinal);
        }
        finally
        {
            TempDirectory.TryDelete(directory);
        }
    }

    [Fact]
    public void Constructor_DeletesFilesOlderThanRetention()
    {
        var directory = TempDirectory.Create();
        try
        {
            var expired = Path.Combine(directory, "proxy-20200101.log");
            var fresh = Path.Combine(directory, "proxy-20260101.log");
            var unrelated = Path.Combine(directory, "keep-me.txt");
            foreach (var file in new[] { expired, fresh, unrelated })
            {
                File.WriteAllText(file, "x");
            }

            File.SetLastWriteTimeUtc(expired, FixedNow.UtcDateTime.AddDays(-30));
            File.SetLastWriteTimeUtc(fresh, FixedNow.UtcDateTime.AddDays(-1));

            using var provider = new FileLoggerProvider(directory, retentionDays: 7, clock: () => FixedNow);

            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            TempDirectory.TryDelete(directory);
        }
    }

    [Fact]
    public void UnusableDirectory_DoesNotThrow()
    {
        var root = TempDirectory.Create();
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        try
        {
            using var provider = new FileLoggerProvider(Path.Combine(blocker, "logs"), clock: () => FixedNow);
            provider.CreateLogger("Cat").LogInformation("still fine");
        }
        finally
        {
            TempDirectory.TryDelete(root);
        }
    }

    /// <summary>
    /// The provider keeps the log file open for writing, so readers must opt into sharing
    /// writes; plain File.ReadAllText would be denied by Windows file sharing rules.
    /// </summary>
    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
