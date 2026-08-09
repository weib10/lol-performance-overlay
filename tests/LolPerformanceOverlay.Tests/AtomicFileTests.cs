using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public async Task CancelledReplacementKeepsLastKnownGoodFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lol-overlay-atomic-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await AtomicFile.WriteAllTextAsync(path, "{\"value\":1}");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicFile.WriteAllTextAsync(path, "{\"value\":2}", cancellation.Token));

            Assert.Equal("{\"value\":1}", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConcurrentReplacementsNeverProducePartialContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lol-overlay-atomic-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var candidates = Enumerable.Range(0, 24)
                .Select(index => $"BEGIN-{index:D2}-" + new string((char)('A' + index), 16_384) + $"-END-{index:D2}")
                .ToArray();

            await Task.WhenAll(candidates.Select(value => AtomicFile.WriteAllTextAsync(path, value)));

            Assert.Contains(await File.ReadAllTextAsync(path), candidates);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
