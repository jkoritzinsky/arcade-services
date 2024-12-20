// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CommandLine;
using Microsoft.DotNet.Darc.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.DotNet.Darc.Options.VirtualMonoRepo;

internal abstract class VmrCommandLineOptions<T> : VmrCommandLineOptionsBase<T> where T : Operation
{
    [Option("tmp", Required = false, HelpText = "Temporary path where intermediate files are stored (e.g. cloned repos, patch files); defaults to usual TEMP.")]
    public string TmpPath { get; set; }

    public override IServiceCollection RegisterServices(IServiceCollection services)
    {
        RegisterVmrServices(services, TmpPath);
        return base.RegisterServices(services);
    }
}
