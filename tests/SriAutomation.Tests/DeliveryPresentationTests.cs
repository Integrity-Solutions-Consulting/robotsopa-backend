using SriAutomation.LocalAgent;

namespace SriAutomation.Tests;

public sealed class DeliveryPresentationTests
{
    [Fact]
    public void ExistingDeliveryStateIsHiddenWithoutChangingItsContentsOrDocumentFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "sri-delivery-presentation-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(root, ".delivery-state");
        var documents = Path.Combine(root, "1790000000001", "2026", "09", "Facturas");
        try
        {
            Directory.CreateDirectory(state); Directory.CreateDirectory(documents);
            var marker = Path.Combine(state, "existing.json"); File.WriteAllText(marker, "existing state");
            var originalAttributes = File.GetAttributes(state);
            DeliveryAgent.HideDeliveryState(root); DeliveryAgent.HideDeliveryState(root);
            Assert.Equal("existing state", File.ReadAllText(marker));
            Assert.Equal(originalAttributes | (OperatingSystem.IsWindows() ? FileAttributes.Hidden : 0), File.GetAttributes(state));
            Assert.False(File.GetAttributes(documents).HasFlag(FileAttributes.Hidden));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
