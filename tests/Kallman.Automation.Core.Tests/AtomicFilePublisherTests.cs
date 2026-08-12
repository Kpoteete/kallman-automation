using Kallman.Automation.Core.Files;

namespace Kallman.Automation.Core.Tests;

public sealed class AtomicFilePublisherTests
{
    [Fact]
    public void Publish_ReplacesDestinationAndPreservesBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "kallman-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string destination = Path.Combine(root, "output.csv");
            string backup = Path.Combine(root, "output.csv.bak");
            string temporary = AtomicFilePublisher.CreateTemporaryPath(destination);
            File.WriteAllText(destination, "old");
            File.WriteAllText(temporary, "new");

            AtomicFilePublisher.Publish(temporary, destination, backup);

            Assert.Equal("new", File.ReadAllText(destination));
            Assert.Equal("old", File.ReadAllText(backup));
            Assert.False(File.Exists(temporary));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Publish_RejectsEmptyOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "kallman-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string destination = Path.Combine(root, "output.csv");
            string temporary = AtomicFilePublisher.CreateTemporaryPath(destination);
            File.WriteAllText(temporary, "");
            Assert.Throws<InvalidDataException>(() => AtomicFilePublisher.Publish(temporary, destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
