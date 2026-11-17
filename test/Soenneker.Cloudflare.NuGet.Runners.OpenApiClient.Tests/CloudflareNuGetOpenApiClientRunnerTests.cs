using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Cloudflare.NuGet.Runners.OpenApiClient.Utils;

namespace Soenneker.Cloudflare.NuGet.Runners.OpenApiClient.Tests;

public sealed class CloudflareNuGetOpenApiClientRunnerTests
{
    [Test]
    public async Task Requires_explicit_generation_target_before_repository_operations()
    {
        var configuration = new ConfigurationBuilder().Build();
        var utility = new FileOperationsUtil(NullLogger<FileOperationsUtil>.Instance, configuration,
            null!, null!, null!, null!, null!, null!, null!, null!);
        try { await utility.Process(); throw new Exception("Expected missing configuration to be rejected."); }
        catch (InvalidOperationException exception)
        {
            await Assert.That(exception.Message.Contains("Configure local document/client paths")).IsTrue();
        }
    }
}
