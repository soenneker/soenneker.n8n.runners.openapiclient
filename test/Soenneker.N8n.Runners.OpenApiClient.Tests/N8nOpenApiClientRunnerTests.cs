using Soenneker.Tests.HostedUnit;

namespace Soenneker.N8n.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class N8nOpenApiClientRunnerTests : HostedUnitTest
{
    public N8nOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
