// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using CollectSFData.Common;
using CollectSFData.DataFile;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using Azure.Storage;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Azure;
using Azure.Core.Extensions;

//https://docs.microsoft.com/en-us/azure/storage/blobs/storage-blobs-list?tabs=dotnet
//https://azuresdkdocs.blob.core.windows.net/$web/dotnet/Azure.Storage.Blobs/12.1.0/api/Azure.Storage.Blobs/Azure.Storage.Blobs.BlobContainerClient.html
namespace CollectSFData.Azure
{
    public class BlobManager : Constants
    {
        private readonly CustomTaskManager _blobChildTasks = new CustomTaskManager(true) { CreationOptions = TaskCreationOptions.AttachedToParent };
        private readonly CustomTaskManager _blobTasks = new CustomTaskManager(true);

        private BlobContainerClient _containerClient;
        private object _dateTimeMaxLock = new object();

        private object _dateTimeMinLock = new object();
        private Instance _instance = Instance.Singleton();
        private BlobServiceClient _serviceClient;
        private ConfigurationOptions Config => _instance.Config;
        public List<BlobContainerItem> ContainerList { get; set; } = new List<BlobContainerItem>();

        public Action<FileObject> IngestCallback { get; set; }

        public bool ReturnSourceFileLink { get; set; }

        public void ConfigureServices(IAzureClientBuilder services)
        {
            services.AddAzureClients(builder =>
            {
                // Add a KeyVault client
                builder.AddSecretClient(Configuration.GetSection("KeyVault"));

                // Add a storage account client
                builder.AddBlobServiceClient(Configuration.GetSection("Storage"));

                // Use the environment credential by default
                builder.UseCredential(new EnvironmentCredential());

                // Set up any default settings
                builder.ConfigureDefaults(Configuration.GetSection("AzureDefaults"));
            });

            services.AddControllers();
        }

        public bool Connect()
        {
            if (!Config.SasEndpointInfo.IsPopulated())
            {
                Log.Warning("no blob or token info. exiting:", Config.SasEndpointInfo);
                return false;
            }

            try
            {
                // no communication with storage account until here:
                EnumerateContainers(null, true);
                return true;
            }
            catch (Exception e)
            {
                Log.Exception($"{e}");
                return false;
            }
        }
        private void AddContainerToList(CloudBlobContainer container)
        {
            if (!ContainerList.Any(x => x.Name.Equals(container.Name)))
            {
                Log.Info($"adding container to list:{container.Name}", ConsoleColor.Green);
                ContainerList.Add(container);
            }
        }

        public void DownloadContainers(string containerPrefix = "")
        {
            EnumerateContainers(containerPrefix);

            foreach (BlobContainerItem container in ContainerList)
            {
                Log.Info($"ContainerName: {container.Name}, NodeFilter: {Config.NodeFilter}");
                DownloadContainer(container);
            }

            Log.Info("waiting for download tasks");
            _blobTasks.Wait();
            _blobChildTasks.Wait();
        }

        private void AddContainerToList(BlobContainerItem containerClient)
        {
            if (!ContainerList.Any(x => x.Name.Equals(containerClient.Name)))
            {
                Log.Info($"adding container to list:{containerClient.Name}", ConsoleColor.Green);
                ContainerList.Add(containerClient);
            }
        }

        private void DownloadBlobsFromContainer(BlobContainerItem blobContainerItem)
        {
            Log.Info($"enumerating:{blobContainerItem.Name}", ConsoleColor.Black, ConsoleColor.Cyan);

            foreach (Page<BlobItem> pageBlobItem in EnumerateContainerBlobs(blobContainerItem))
            {
                _blobTasks.TaskAction(() => QueueBlobPageDownload(pageBlobItem));
            }
        }

        private void DownloadBlobsFromDirectory(BlobContainerClient blobContainerClient)
        {
            Log.Info($"enumerating:{blobContainerClient.Uri}", ConsoleColor.Cyan);

            foreach (Page<BlobItem> pageBlobItem in EnumerateDirectoryBlobs(blobContainerClient))
            {
                _blobChildTasks.TaskAction(() => QueueBlobPageDownload(pageBlobItem));
            }
        }

        private void DownloadContainer(BlobContainerItem container)
        {
            Log.Info($"enter:{container.Name}");
            DownloadBlobsFromContainer(container);
        }

        public void DownloadFiles(List<string> uris)
        {
            List<IListBlobItem> blobItems = new List<IListBlobItem>();

            foreach (string uri in uris)
            {
                try
                {
                    blobItems.Add(_blobClient.GetBlobReferenceFromServer(new Uri(uri)));
                }
                catch (Exception e)
                {
                    Log.Exception($"{e}");
                }
            }

            QueueBlobSegmentDownload(blobItems);
        }

        private IEnumerable<Page<BlobItem>> EnumerateContainerBlobs(BlobContainerItem container)
        {
            Log.Info($"enter: {container.Name}");
            Page<BlobItem> pageBlobItem = default(Page<BlobItem>);
            CancellationToken cancelToken = new CancellationToken();
            string blobToken = "";

            while (true)
            {
                pageBlobItem = _blobTasks.TaskFunction((pageblobitem) =>
                _containerClient.GetBlobsByHierarchyAsync(
                        BlobTraits.None,
                        BlobStates.None,
                        null,
                        container.Name,
                        cancelToken)
                    .AsPages(blobToken)).Result as Page<BlobItem>;

                blobToken = pageBlobItem.ContinuationToken;
                yield return pageBlobItem;

                if (string.IsNullOrEmpty(blobToken))
                {
                    break;
                }
            }

            Log.Info($"exit {container.Name}");
        }

        private void EnumerateContainers(string containerPrefix = "", bool testConnectivity = false)
        {
            string connectionString = Config.SasEndpointInfo.ConnectionString;

            string blobToken = "";
            CancellationToken cancelToken = new CancellationToken();

            string containerFilter = Config.ContainerFilter ?? string.Empty;
            IAsyncEnumerable<Page<BlobContainerItem>> containerPages = default(IAsyncEnumerable<Page<BlobContainerItem>>);
            Page<BlobContainerItem> containerPage = default(Page<BlobContainerItem>);
            Log.Info("account sas");

            while (blobToken != null)
            {
                Log.Info($"containerPrefix:{containerPrefix} containerFilter:{containerFilter}");

                try
                {
                    var bco = new BlobClientOptions(BlobClientOptions.ServiceVersion.V2020_02_10);
                    bco.AddBlobServiceClient(connectionString);

                    _serviceClient = new BlobServiceClient(connectionString, bco);

                    containerPages = _serviceClient.GetBlobContainersAsync(
                        BlobContainerTraits.None,
                        BlobContainerStates.None,
                        prefix: containerPrefix,
                        cancelToken).AsPages(blobToken);

                    if (testConnectivity)
                    {
                        return;
                    }

                    IAsyncEnumerator<Page<BlobContainerItem>> containerPageEnumerator = containerPages.GetAsyncEnumerator();
                    containerPage = containerPageEnumerator.Current;

                    while (containerPage != null && containerPageEnumerator.MoveNextAsync().Result)
                    {
                        IEnumerable<BlobContainerItem> containers = containerPage.Values.Where(x => Regex.IsMatch(x.Name, containerFilter, RegexOptions.IgnoreCase));
                        Log.Info("container list results:", containers.Select(x => x.Name));

                        if (containers.Count() < 1)
                        {
                            Log.Warning($"no containers detected. use 'containerFilter' argument to specify");
                        }

                        if (containers.Count() > 1)
                        {
                            Log.Warning($"multiple containers detected. use 'containerFilter' argument to enumerate only one");
                        }

                        foreach (BlobContainerItem container in containers)
                        {
                            AddContainerToList(container);
                        }

                        blobToken = containerPageEnumerator.Current.ContinuationToken;
                        if (string.IsNullOrEmpty(blobToken))
                        {
                            break;
                        }
                    }
                    return;
                }
                catch (Exception e)
                {
                    Log.Debug($"{e}");
                    Log.Warning($"unable to connect to containerPrefix: {containerPrefix} containerFilter: {containerFilter} error: {e.HResult}");

                    if (containerPage == null && !string.IsNullOrEmpty(containerPrefix))
                    {
                        Log.Warning("retrying without containerPrefix");
                        containerPrefix = null;
                    }

                    if (containerPage == null && (Config.SasEndpointInfo.AbsolutePath.Length > 1) && !connectionString.Contains(Config.SasEndpointInfo.AbsolutePath))
                    {
                        Log.Info("absolute path sas");
                        Log.Warning("retrying with absolute path");
                        connectionString = Config.SasEndpointInfo.AbsolutePath + "?" + Config.SasEndpointInfo.SasToken;
                    }
                    else
                    {
                        string errMessage = "unable to enumerate containers with or without absolute path";
                        Log.Error(errMessage);
                        throw new Exception(errMessage);
                    }
                }
            }
        }

        private IEnumerable<Page<BlobItem>> EnumerateDirectoryBlobs(BlobContainerClient containerClient)
        {
            Log.Info($"enter {containerClient.Uri}");
            Page<BlobItem> pageBlobItem = default(Page<BlobItem>);
            CancellationToken cancelToken = new CancellationToken();
            string blobToken = "";

            while (true)
            {
                pageBlobItem = _blobChildTasks.TaskFunction((pageBlobItems) =>
                    containerClient.GetBlobsByHierarchyAsync(
                        BlobTraits.None,
                        BlobStates.None,
                        null,
                        null,
                        cancelToken)
                    .AsPages(blobToken)).Result as Page<BlobItem>;

                blobToken = pageBlobItem.ContinuationToken;
                yield return pageBlobItem;

                if (string.IsNullOrEmpty(blobToken))
                {
                    break;
                }
            }

            Log.Info($"exit {containerClient.Uri}");
        }

        private void InvokeCallback(BlobClient blobClient, FileObject fileObject, int sourceLength)
        {
            if (!fileObject.Exists)
            {
                BlobClientOptions blobRequestOptions = new BlobClientOptions()
                {
                    //RetryPolicy = new IngestRetryPolicy(),
                    //ParallelOperationThreadCount = Config.Threads
                };

                if (sourceLength > MaxStreamTransmitBytes)
                {
                    fileObject.DownloadAction = () =>
                    {
                        if (!Directory.Exists(Path.GetDirectoryName(fileObject.FileUri)))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(fileObject.FileUri));
                        }

                        //blobClient.DownloadToFileAsync(fileObject.FileUri, FileMode.Create, null, blobRequestOptions, null).Wait();
                        blobClient.DownloadToAsync(fileObject.FileUri, FileMode.Create, null, blobRequestOptions, null).Wait();
                    };
                }
                else
                {
                    fileObject.DownloadAction = () =>
                    {
                        //blobClient.DownloadToStreamAsync(fileObject.Stream.Get(), null, blobRequestOptions, null).Wait();
                        blobClient.DownloadToAsync(fileObject.Stream.Get(), null, new StorageTransferOptions { MaximumConcurrency = Config.Threads }).Wait();
                    };
                }

                IngestCallback?.Invoke(fileObject);
                Interlocked.Increment(ref _instance.TotalFilesDownloaded);
            }
            else
            {
                Log.Warning($"destination file exists. skipping download:\r\n file: {fileObject}");
                Interlocked.Increment(ref _instance.TotalFilesSkipped);
            }
        }

        private void QueueBlobPageDownload(Page<BlobItem> blobItemPage)
        {
            int parentId = Thread.CurrentThread.ManagedThreadId;
            Log.Debug($"enter. current id:{parentId}. results count: {blobItemPage.Values.Count()}");
            long segmentMinDateTicks = Interlocked.Read(ref DiscoveredMinDateTicks);
            long segmentMaxDateTicks = Interlocked.Read(ref DiscoveredMaxDateTicks);

            foreach (BlobItem blob in blobItemPage.Values)
            {
                BlobClient blobClient = default(BlobClient);

                try
                {
                    blobClient = _containerClient.GetBlobClient(blob.Name);
                    Log.Debug($"file Blob: {blob.Name}");
                }
                catch (Exception se)
                {
                    Interlocked.Increment(ref _instance.TotalErrors);
                    Log.Exception($"getting ref for {blobClient.Uri}, skipping. {se.Message}");
                    continue;
                }

                Log.Debug($"parent id:{parentId} current Id:{Thread.CurrentThread.ManagedThreadId}");
                if (!string.IsNullOrEmpty(Config.NodeFilter) && !Regex.IsMatch(blobClient.Uri.ToString(), Config.NodeFilter, RegexOptions.IgnoreCase))
                {
                    Log.Debug($"blob:{blobClient.Uri} does not match nodeFilter pattern:{Config.NodeFilter}, skipping...");
                    continue;
                }

                Interlocked.Increment(ref _instance.TotalFilesEnumerated);

                if (Regex.IsMatch(blobClient.Uri.ToString(), FileFilterPattern, RegexOptions.IgnoreCase))
                {
                    long ticks = Convert.ToInt64(Regex.Match(blobClient.Uri.ToString(), FileFilterPattern, RegexOptions.IgnoreCase).Groups[1].Value);

                    if (ticks < Config.StartTimeUtc.Ticks | ticks > Config.EndTimeUtc.Ticks)
                    {
                        Interlocked.Increment(ref _instance.TotalFilesSkipped);
                        Log.Debug($"exclude:bloburi file ticks {new DateTime(ticks).ToString("o")} outside of time range:{blobClient.Uri}");

                        SetMinMaxDate(ref segmentMinDateTicks, ref segmentMaxDateTicks, ticks);
                        continue;
                    }
                }
                else
                {
                    Log.Debug($"regex not matched: {blobClient.Uri} pattern: {FileFilterPattern}");
                }

                try
                {
                    Log.Debug($"file Blob: {blob.Uri}");
                    blobRef = blob.Container.ServiceClient.GetBlobReferenceFromServerAsync(blob.Uri).Result;
                }
                catch (StorageException se)
                {
                    Interlocked.Increment(ref _instance.TotalErrors);
                    Log.Exception($"getting ref for {blob.Uri}, skipping. {se.Message}");
                    continue;
                }

                if (blob.Properties.LastModified.HasValue)
                {
                    DateTimeOffset lastModified = blob.Properties.LastModified.Value;
                    SetMinMaxDate(ref segmentMinDateTicks, ref segmentMaxDateTicks, lastModified.Ticks);

                    if (!string.IsNullOrEmpty(Config.UriFilter) && !Regex.IsMatch(blobClient.Uri.ToString(), Config.UriFilter, RegexOptions.IgnoreCase))
                    {
                        Interlocked.Increment(ref _instance.TotalFilesSkipped);
                        Log.Debug($"blob:{blob.Name} does not match uriFilter pattern:{Config.UriFilter}, skipping...");
                        continue;
                    }

                    if (Config.FileType != FileTypesEnum.any
                        && !FileTypes.MapFileTypeUri(blobClient.Uri.AbsolutePath).Equals(Config.FileType))
                    {
                        Interlocked.Increment(ref _instance.TotalFilesSkipped);
                        Log.Debug($"skipping uri with incorrect file type: {FileTypes.MapFileTypeUri(blobClient.Uri.AbsolutePath)}");
                        continue;
                    }

                    if (lastModified >= Config.StartTimeUtc && lastModified <= Config.EndTimeUtc)
                    {
                        Interlocked.Increment(ref _instance.TotalFilesMatched);

                        if (Config.List)
                        {
                            Log.Info($"listing file with timestamp: {lastModified}\r\n file: {blobClient.Uri.AbsolutePath}");
                            continue;
                        }

                        if (ReturnSourceFileLink)
                        {
                            IngestCallback?.Invoke(new FileObject(blobClient.Uri.AbsolutePath, Config.SasEndpointInfo.BlobEndpoint)
                            {
                                LastModified = lastModified
                            });
                            continue;
                        }

                        FileObject fileObject = new FileObject(blobClient.Uri.AbsolutePath, Config.CacheLocation)
                        {
                            LastModified = lastModified
                        };

                        Log.Info($"queueing blob with timestamp: {lastModified}\r\n file: {blobClient.Uri.AbsolutePath}");
                        InvokeCallback(blobClient, fileObject, (int)blob.Properties.ContentLength);
                    }
                    else
                    {
                        Interlocked.Increment(ref _instance.TotalFilesSkipped);
                        Log.Debug($"exclude:bloburi {lastModified.ToString("o")} outside of time range:{blob.Name}");

                        SetMinMaxDate(ref segmentMinDateTicks, ref segmentMaxDateTicks, lastModified.Ticks);
                        continue;
                    }
                }
                else
                {
                    Log.Error("unable to read blob modified date", blob);
                    _instance.TotalErrors++;
                }
            }
        }

        private void SetMinMaxDate(ref long segmentMinDateTicks, ref long segmentMaxDateTicks, long ticks)
        {
            if (ticks > DateTime.MinValue.Ticks && ticks < DateTime.MaxValue.Ticks)
            {
                if (ticks < segmentMinDateTicks)
                {
                    Log.Debug($"set new discovered min time range ticks: {new DateTime(ticks).ToString("o")}");
                    lock (_dateTimeMinLock)
                    {
                        segmentMinDateTicks = DiscoveredMinDateTicks = Math.Min(DiscoveredMinDateTicks, ticks);
                    }
                }

                if (ticks > segmentMaxDateTicks)
                {
                    Log.Debug($"set new discovered max time range ticks: {new DateTime(ticks).ToString("o")}");
                    lock (_dateTimeMaxLock)
                    {
                        segmentMaxDateTicks = DiscoveredMaxDateTicks = Math.Max(DiscoveredMaxDateTicks, ticks);
                    }
                }
            }
        }
    }
}