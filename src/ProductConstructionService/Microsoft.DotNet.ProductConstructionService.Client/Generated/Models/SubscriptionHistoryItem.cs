// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Newtonsoft.Json;

namespace Microsoft.DotNet.ProductConstructionService.Client.Models
{
    public partial class SubscriptionHistoryItem
    {
        public SubscriptionHistoryItem(DateTimeOffset timestamp, bool success, Guid subscriptionId, string errorMessage, string action, string retryUrl)
        {
            Timestamp = timestamp;
            Success = success;
            SubscriptionId = subscriptionId;
            ErrorMessage = errorMessage;
            Action = action;
            RetryUrl = retryUrl;
        }

        [JsonProperty("timestamp")]
        public DateTimeOffset Timestamp { get; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; }

        [JsonProperty("success")]
        public bool Success { get; }

        [JsonProperty("subscriptionId")]
        public Guid SubscriptionId { get; }

        [JsonProperty("action")]
        public string Action { get; }

        [JsonProperty("retryUrl")]
        public string RetryUrl { get; }
    }
}
