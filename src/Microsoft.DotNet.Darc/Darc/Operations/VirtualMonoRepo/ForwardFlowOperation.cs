﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Darc.Options.VirtualMonoRepo;
using Microsoft.DotNet.DarcLib;
using Microsoft.DotNet.DarcLib.Helpers;
using Microsoft.DotNet.DarcLib.VirtualMonoRepo;
using Microsoft.Extensions.Logging;

#nullable enable
namespace Microsoft.DotNet.Darc.Operations.VirtualMonoRepo;

internal class ForwardFlowOperation(
    ForwardFlowCommandLineOptions options,
    IVmrForwardFlower vmrForwardFlower,
    IVmrInfo vmrInfo,
    IVmrDependencyTracker dependencyTracker,
    ILocalGitRepoFactory localGitRepoFactory,
    ILogger<ForwardFlowOperation> logger)
    : CodeFlowOperation(options, vmrInfo, dependencyTracker, localGitRepoFactory, logger)
{
    private readonly ForwardFlowCommandLineOptions _options = options;

    protected override async Task<bool> FlowAsync(
        string mappingName,
        NativePath repoPath,
        CancellationToken cancellationToken)
    {
        return await vmrForwardFlower.FlowForwardAsync(
            mappingName,
            repoPath,
            _options.Build ?? throw new Exception("Please specify a build to flow"),
            excludedAssets: null,
            await GetBaseBranch(new NativePath(_options.VmrPath)),
            await GetTargetBranch(repoPath),
            _options.DiscardPatches,
            cancellationToken);
    }
}
