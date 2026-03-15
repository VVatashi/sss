namespace SimpleShadowsocks.Client.Tests;

[Trait(TestCategories.Name, TestCategories.Integration)]
public sealed partial class TunnelIntegrationTests : IDisposable
{
    private readonly ConsoleSilencer _consoleSilencer = new();

    public void Dispose()
    {
        _consoleSilencer.Dispose();
    }
}
