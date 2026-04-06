using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Soenneker.Extensions.String;
using Soenneker.Playwrights.Extensions.Stealth;
using Soenneker.Playwrights.Installation.Abstract;
using Soenneker.Git.Util.Abstract;
using Soenneker.N8n.Runners.OpenApiClient.Utils.Abstract;
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
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using System.Collections.Generic;

namespace Soenneker.N8n.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IPlaywrightInstallationUtil _playwrightInstallationUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IConfiguration configuration, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IPlaywrightInstallationUtil playwrightInstallationUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IKiotaUtil kiotaUtil)
    {
        _logger = logger;
        _configuration = configuration;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _playwrightInstallationUtil = playwrightInstallationUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string targetFilePath = Path.Combine(gitDirectory, "openapi.json");

        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string openApiDocumentUrl = _configuration["N8n:ClientGenerationUrl"] ?? "https://docs.n8n.io/api/api-reference/";

        string filePath = await DownloadOpenApiDocument(openApiDocumentUrl, targetFilePath, cancellationToken);

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(filePath, "N8nOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    private async ValueTask<string> DownloadOpenApiDocument(string openApiDocumentUrl, string targetFilePath, CancellationToken cancellationToken)
    {
        await _playwrightInstallationUtil.EnsureInstalled(cancellationToken).NoSync();

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.LaunchStealthChromium(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        await using IBrowserContext context = await browser.CreateStealthContext(new BrowserNewContextOptions
        {
            AcceptDownloads = true
        }).NoSync();

        IPage page = await context.NewPageAsync();
        page.SetDefaultTimeout(60000);
        page.SetDefaultNavigationTimeout(60000);

        await page.AddInitScriptAsync("""
            (() => {
                const originalCreateObjectURL = URL.createObjectURL.bind(URL);

                window.__soennekerLatestBlobUrl = null;

                URL.createObjectURL = function(blob) {
                    const url = originalCreateObjectURL(blob);
                    window.__soennekerLatestBlobUrl = url;
                    return url;
                };
            })();
            """);

        _logger.LogInformation("Navigating to n8n API reference page: {Url}", openApiDocumentUrl);

        await page.GotoAsync(openApiDocumentUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load
        });

        _logger.LogInformation("Waiting for OpenAPI download button...");

        ILocator downloadButton = page.Locator("div.download-container.download-both button.download-button").Nth(0);
        await downloadButton.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible
        });

        await downloadButton.ScrollIntoViewIfNeededAsync();

        _logger.LogInformation("Clicking JSON OpenAPI download button...");

        await page.EvaluateAsync("""
            () => {
                window.__soennekerLatestBlobUrl = null;
            }
            """);

        await downloadButton.ClickAsync(new LocatorClickOptions
        {
            Force = true
        });

        _logger.LogInformation("Waiting for generated OpenAPI blob...");

        await page.WaitForFunctionAsync("""
            () => typeof window.__soennekerLatestBlobUrl === 'string' && window.__soennekerLatestBlobUrl.startsWith('blob:')
            """, null, new PageWaitForFunctionOptions
        {
            Timeout = 30000
        });

        string? json = await page.EvaluateAsync<string>("""
            async () => {
                const blobUrl = window.__soennekerLatestBlobUrl;

                if (!blobUrl)
                    return null;

                const response = await fetch(blobUrl);
                return await response.text();
            }
            """);

        if (string.IsNullOrWhiteSpace(json))
            throw new Exception("OpenAPI blob was generated, but no JSON content could be read from it.");

        await File.WriteAllTextAsync(targetFilePath, json, cancellationToken);

        _logger.LogInformation("Saved OpenAPI document to {TargetFilePath}", targetFilePath);

        return targetFilePath;
    }

    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}
