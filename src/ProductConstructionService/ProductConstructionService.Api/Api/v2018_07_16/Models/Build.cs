// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable
namespace ProductConstructionService.Api.v2018_07_16.Models;

public class Build
{
    public Build(Maestro.Data.Models.Build other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var hasGitHubInfo = other.GitHubRepository != null;

        Id = other.Id;
        Repository = hasGitHubInfo
            ? other.GitHubRepository
            : other.AzureDevOpsRepository;
        Branch = hasGitHubInfo
            ? other.GitHubBranch
            : other.AzureDevOpsBranch;
        Commit = other.Commit;
        BuildNumber = other.AzureDevOpsBuildNumber;
        DateProduced = other.DateProduced;
        Channels = other.BuildChannels?.Select(bc => bc.Channel)
            .Where(c => c != null)
            .Select(c => new Channel(c))
            .ToList();
        Assets = other.Assets?.Select(a => new Asset(a)).ToList();
    }

    public int Id { get; }

    public string Repository { get; }

    public string Branch { get; }

    public string Commit { get; }

    public string BuildNumber { get; }

    public DateTimeOffset DateProduced { get; }

    public List<Channel> Channels { get; }

    public List<Asset> Assets { get; }

    public List<BuildRef> Dependencies { get; }
}

public class BuildRef
{
    public int Id { get; set; }
}
