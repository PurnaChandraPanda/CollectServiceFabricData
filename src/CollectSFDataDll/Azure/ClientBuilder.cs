// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Azure.Core.Extensions;

//https://docs.microsoft.com/en-us/azure/storage/blobs/storage-blobs-list?tabs=dotnet
//https://azuresdkdocs.blob.core.windows.net/$web/dotnet/Azure.Storage.Blobs/12.1.0/api/Azure.Storage.Blobs/Azure.Storage.Blobs.BlobContainerClient.html
namespace CollectSFData.Azure
{
    public class ClientBuilder<TClient, TOptions> where TOptions: class, IAzureClientBuilder<TClient, TOptions>, IAzureClientFactoryBuilderWithConfiguration<TClient>
    {
        public ClientBuilder()
        {
        }
    }
}