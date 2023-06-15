﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Microsoft.TemplateEngine.Cli.NuGet
{
    internal class NugetApiManager
    {
        private const string _nugetOrgFeed = "https://api.nuget.org/v3/index.json";
        private readonly PackageSource _nugetOrgSource = new PackageSource(_nugetOrgFeed);
        private readonly IDictionary<PackageSource, SourceRepository> _sourceRepositories;
        private readonly SourceCacheContext _cacheSettings = new SourceCacheContext()
        {
            NoCache = true,
            DirectDownload = true
        };

        private readonly ILogger _nugetLogger = NullLogger.Instance;

        internal NugetApiManager()
        {
            _sourceRepositories = new Dictionary<PackageSource, SourceRepository>();
        }

        public async Task<NugetPackageMetadata?> GetPackageMetadataAsync(
            string packageIdentifier,
            string? packageVersion = null,
            PackageSource? sourceFeed = null,
            CancellationToken cancellationToken = default)
        {
            if (sourceFeed == null)
            {
                sourceFeed = _nugetOrgSource;
            }

            SourceRepository repository = GetSourceRepository(sourceFeed);
            PackageMetadataResource resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
            IEnumerable<IPackageSearchMetadata> packagesMetadata = await resource.GetMetadataAsync(
                packageIdentifier,
                includePrerelease: true,
                includeUnlisted: false,
                _cacheSettings,
                _nugetLogger,
                cancellationToken).ConfigureAwait(false);

            IPackageSearchMetadata? matchedPackage = null;
            if (!string.IsNullOrWhiteSpace(packageVersion))
            {
                matchedPackage = packagesMetadata.FirstOrDefault(pm => pm.Identity.Version == new NuGetVersion(packageVersion));
            }
            else
            {
                var floatRange = new FloatRange(NuGetVersionFloatBehavior.AbsoluteLatest);
                matchedPackage = packagesMetadata.Aggregate(
                    null,
                    (IPackageSearchMetadata? max, IPackageSearchMetadata current) =>
                        ((max != null &&
                        !(current.Identity.Version > max!.Identity.Version))
                        || !floatRange.Satisfies(current.Identity.Version))
                            ? max
                            : current
                    );
            }

            return matchedPackage == default
                ? null
                : new NugetPackageMetadata(
                    matchedPackage,
                    await GetPackageOwners(repository, packageIdentifier, cancellationToken).ConfigureAwait(false),
                    sourceFeed);
        }

        private async Task<string> GetPackageOwners(
            SourceRepository repository,
            string packageIdentifier,
            CancellationToken cancellationToken)
        {
            var nugetSearchClient = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);

            var searchResult = (await nugetSearchClient.SearchAsync(
                packageIdentifier,
                new SearchFilter(includePrerelease: false),
                skip: 0,
                take: 1,
                _nugetLogger,
                cancellationToken).ConfigureAwait(false))
                .FirstOrDefault();

            return searchResult != null ? searchResult.Owners : string.Empty;
        }

        private SourceRepository GetSourceRepository(PackageSource source)
        {
            if (!_sourceRepositories.ContainsKey(source))
            {
                _sourceRepositories.Add(source, Repository.Factory.GetCoreV3(source));
            }

            return _sourceRepositories[source];
        }

        internal class NugetPackageMetadata
        {
            public NugetPackageMetadata(IPackageSearchMetadata metadata, string owners, PackageSource packageSource)
            {
                Authors = metadata.Authors;
                Identity = metadata.Identity;
                Owners = owners;
                Description = metadata.Description;
                ProjectUrl = metadata.ProjectUrl;
                LicenseUrl = metadata.LicenseUrl;
                License = metadata.LicenseMetadata?.License;
                Identity = metadata.Identity;
                LicenseExpression = metadata.LicenseMetadata?.LicenseExpression.ToString();
                Source = packageSource;
                PackageVersion = metadata.Identity.Version;
            }

            public string? Description { get; }

            public Uri? LicenseUrl { get; }

            public string? License { get; }

            public string? LicenseExpression { get; }

            public Uri? ProjectUrl { get; }

            public string Authors { get; }

            public PackageIdentity Identity { get; }

            public string Owners { get; }

            public PackageSource Source { get; }

            public NuGetVersion PackageVersion { get; }
        }
    }
}
