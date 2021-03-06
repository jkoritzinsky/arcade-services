// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using System.Data;

namespace Microsoft.DotNet.Kusto
{
    public interface IKustoClientProvider
    {
        Task<IDataReader> ExecuteKustoQueryAsync(KustoQuery query);
    }
}