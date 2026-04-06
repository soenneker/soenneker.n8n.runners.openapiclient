using Soenneker.Tests.FixturedUnit;
using Xunit;

namespace Soenneker.N8n.Runners.OpenApiClient.Tests;

[Collection("Collection")]
public sealed class N8nOpenApiClientRunnerTests : FixturedUnitTest
{
    public N8nOpenApiClientRunnerTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }

    [Fact]
    public void Default()
    {

    }
}
