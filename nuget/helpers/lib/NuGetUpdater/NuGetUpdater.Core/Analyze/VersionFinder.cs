using System.Collections.Immutable;

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGetUpdater.Core.Analyze;

internal static class VersionFinder
{
    public static Task<VersionResult> GetVersionsAsync(
        string packageId,
        NuGetVersion currentVersion,
        NuGetContext nugetContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var versionFilter = CreateVersionFilter(currentVersion);

        return GetVersionsAsync(packageId, currentVersion, versionFilter, nugetContext, logger, cancellationToken);
    }

    public static Task<VersionResult> GetVersionsAsync(
        DependencyInfo dependencyInfo,
        NuGetContext nugetContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var packageId = dependencyInfo.Name;
        var versionRange = VersionRange.Parse(dependencyInfo.Version);
        var currentVersion = versionRange.MinVersion!;
        var versionFilter = CreateVersionFilter(dependencyInfo, versionRange);

        return GetVersionsAsync(packageId, currentVersion, versionFilter, nugetContext, logger, cancellationToken);
    }

    public static async Task<VersionResult> GetVersionsAsync(
        string packageId,
        NuGetVersion currentVersion,
        Func<NuGetVersion, bool> versionFilter,
        NuGetContext nugetContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var includePrerelease = currentVersion.IsPrerelease;
        VersionResult result = new(currentVersion);

        var sourceMapping = PackageSourceMapping.GetPackageSourceMapping(nugetContext.Settings);
        var packageSources = sourceMapping.GetConfiguredPackageSources(packageId).ToHashSet();
        var sources = packageSources.Count == 0
            ? nugetContext.PackageSources
            : nugetContext.PackageSources
                .Where(p => packageSources.Contains(p.Name))
                .ToImmutableArray();

        foreach (var source in sources)
        {
            var sourceRepository = Repository.Factory.GetCoreV3(source);
            var feed = await sourceRepository.GetResourceAsync<MetadataResource>();
            if (feed is null)
            {
                logger.Warn($"Failed to get MetadataResource for [{source.Source}]");
                continue;
            }

            try
            {
                // a non-compliant v2 API returning 404 can cause this to throw
                var existsInFeed = await feed.Exists(
                    packageId,
                    includePrerelease,
                    includeUnlisted: false,
                    nugetContext.SourceCacheContext,
                    NullLogger.Instance,
                    cancellationToken);
                if (!existsInFeed)
                {
                    continue;
                }
            }
            catch (FatalProtocolException)
            {
                // if anything goes wrong here, the package source obviously doesn't contain the requested package
                continue;
            }

            var feedVersions = (await feed.GetVersions(
                packageId,
                includePrerelease,
                includeUnlisted: false,
                nugetContext.SourceCacheContext,
                NullLogger.Instance,
                CancellationToken.None)).ToHashSet();

            if (feedVersions.Contains(currentVersion))
            {
                result.AddCurrentVersionSource(source);
            }

            result.AddRange(source, feedVersions.Where(versionFilter));
        }

        return result;
    }

    internal static Func<NuGetVersion, bool> CreateVersionFilter(DependencyInfo dependencyInfo, VersionRange versionRange)
    {
        // If we are floating to the absolute latest version, we should not filter pre-release versions at all.
        var currentVersion = versionRange.Float?.FloatBehavior != NuGetVersionFloatBehavior.AbsoluteLatest
            ? versionRange.MinVersion
            : null;

        var safeVersions = dependencyInfo.Vulnerabilities.SelectMany(v => v.SafeVersions).ToList();
        return version =>
        {
            if (safeVersions.Any())
            {
                // only consider these
                if (safeVersions.Any(s => s.IsSatisfiedBy(version)))
                {
                    return true;
                }

                return false;
            }
            else
            {
                var versionGreaterThanCurrent = currentVersion is null || version > currentVersion;
                var rangeSatisfies = versionRange.Satisfies(version);
                var prereleaseTypeMatches = currentVersion is null || !currentVersion.IsPrerelease || !version.IsPrerelease || version.Version == currentVersion.Version;
                var isIgnoredVersion = dependencyInfo.IgnoredVersions.Any(i => i.IsSatisfiedBy(version));
                var isVulnerableVersion = dependencyInfo.Vulnerabilities.Any(v => v.IsVulnerable(version));
                return versionGreaterThanCurrent
                    && rangeSatisfies
                    && prereleaseTypeMatches
                    && !isIgnoredVersion
                    && !isVulnerableVersion;
            }
        };
    }

    internal static Func<NuGetVersion, bool> CreateVersionFilter(NuGetVersion currentVersion)
    {
        return version => version > currentVersion
            && (currentVersion is null || !currentVersion.IsPrerelease || !version.IsPrerelease || version.Version == currentVersion.Version);
    }

    public static async Task<bool> DoVersionsExistAsync(
        IEnumerable<string> packageIds,
        NuGetVersion version,
        NuGetContext nugetContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var packageId in packageIds)
        {
            if (!await DoesVersionExistAsync(packageId, version, nugetContext, logger, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    public static async Task<bool> DoesVersionExistAsync(
        string packageId,
        NuGetVersion version,
        NuGetContext nugetContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // if it can be downloaded, it exists
        var downloader = await CompatibilityChecker.DownloadPackageAsync(new PackageIdentity(packageId, version), nugetContext, cancellationToken);
        var packageAndVersionExists = downloader is not null;
        if (packageAndVersionExists)
        {
            // release the handles
            var readers = downloader.GetValueOrDefault();
            (readers.CoreReader as IDisposable)?.Dispose();
            (readers.ContentReader as IDisposable)?.Dispose();
        }

        return packageAndVersionExists;
    }
}
