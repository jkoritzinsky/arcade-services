// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace Microsoft.DotNet.DarcLib.Models.AzureDevOps;

public class AzureDevOpsComment
{
    public AzureDevOpsComment(List<AzureDevOpsCommentBody> comments)
    {
        Comments = comments;
    }

    public List<AzureDevOpsCommentBody> Comments { get; set; }
}

public class AzureDevOpsCommentBody
{
    public AzureDevOpsCommentBody(string content)
    {
        Content = content;
    }

    public string Content { get; set; }

    public int CommentType { get; set; } = 1;
}
