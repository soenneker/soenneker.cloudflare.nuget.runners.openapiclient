using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Cloudflare.NuGet.Runners.OpenApiClient.Utils.Abstract;

/// <summary>Regenerates the Cloudflare NuGet client from its OpenAPI contract.</summary>
public interface IFileOperationsUtil
{
    /// <summary>Uses configured local document/client paths, or publishes repository changes only when explicitly configured.</summary>
    ValueTask Process(CancellationToken cancellationToken = default);
}
