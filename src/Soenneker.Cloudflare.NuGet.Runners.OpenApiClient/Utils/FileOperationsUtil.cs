using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.Cloudflare.NuGet.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.Process.Abstract;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Utils.Yaml.Abstract;
using System.Collections.Generic;

namespace Soenneker.Cloudflare.NuGet.Runners.OpenApiClient.Utils;

public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IYamlUtil _yamlUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IConfiguration configuration, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IFileDownloadUtil fileDownloadUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IKiotaUtil kiotaUtil, IOpenApiFixer openApiFixer,
        IYamlUtil yamlUtil)
    {
        _logger = logger;
        _configuration = configuration;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _openApiFixer = openApiFixer;
        _fileDownloadUtil = fileDownloadUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _yamlUtil = yamlUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string? localDocument = _configuration["Cloudflare:NuGet:OpenApiDocument"];
        string? localClient = _configuration["Cloudflare:NuGet:ClientDirectory"];
        if (!string.IsNullOrWhiteSpace(localDocument) || !string.IsNullOrWhiteSpace(localClient))
        {
            if (string.IsNullOrWhiteSpace(localDocument) || string.IsNullOrWhiteSpace(localClient))
                throw new InvalidOperationException("Both Cloudflare:NuGet:OpenApiDocument and Cloudflare:NuGet:ClientDirectory are required for local generation.");
            await GenerateLocal(localDocument, localClient, cancellationToken);
            return;
        }
        if (!string.Equals(_configuration["Cloudflare:NuGet:PublishGeneratedClient"], "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configure local document/client paths, or explicitly enable Cloudflare:NuGet:PublishGeneratedClient for repository publishing.");

        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string targetFilePath = Path.Combine(gitDirectory, "openapi.json");

        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string openApiDocumentUrl = _configuration["Cloudflare:NuGet:ClientGenerationUrl"] ?? "https://raw.githubusercontent.com/soenneker/soenneker.cloudflare.nuget.openapi/main/openapi.json";

        string? filePath = await _fileDownloadUtil.Download(openApiDocumentUrl,
            targetFilePath, fileExtension: ".json", cancellationToken: cancellationToken);

        if (filePath == null)
            throw new InvalidOperationException("NuGet OpenAPI document download failed.");

        string rawDocument = await _fileUtil.Read(filePath, cancellationToken: cancellationToken);
        string trimmedDocument = rawDocument.TrimStart();

        if (!trimmedDocument.StartsWith('{') && !trimmedDocument.StartsWith('['))
        {
            string convertedFilePath = Path.Combine(gitDirectory, "openapi.converted.json");
            await _fileUtil.DeleteIfExists(convertedFilePath, cancellationToken: cancellationToken);
            await _yamlUtil.SaveAsJson(filePath, convertedFilePath, cancellationToken: cancellationToken);
            filePath = convertedFilePath;
        }

        string fixedFilePath = Path.Combine(gitDirectory, "openapi.fixed.json");
        await _fileUtil.DeleteIfExists(fixedFilePath, cancellationToken: cancellationToken);
        await _openApiFixer.Fix(filePath, fixedFilePath, cancellationToken).NoSync();

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await GenerateLocal(fixedFilePath, srcDirectory, cancellationToken);

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    private static async Task GenerateLocal(string document, string clientDirectory, CancellationToken cancellationToken)
    {
        document = Path.GetFullPath(document);
        clientDirectory = Path.GetFullPath(clientDirectory);
        if (!File.Exists(document) || !File.Exists(Path.Combine(clientDirectory, Constants.Library + ".csproj")))
            throw new InvalidOperationException("The OpenAPI document and target client project must exist.");
        var start = new System.Diagnostics.ProcessStartInfo("kiota") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "generate", "-d", document, "-l", "CSharp", "-c", "CloudflareNuGetOpenApiClient", "-n", Constants.Library,
                     "-o", Path.Combine(clientDirectory, "Generated"), "--exclude-backward-compatible", "--clean-output" })
            start.ArgumentList.Add(argument);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start Kiota.");
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Kiota exited with code {process.ExitCode}.");
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
            throw new InvalidOperationException("Generated client failed to build; no changes were published.");

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}
