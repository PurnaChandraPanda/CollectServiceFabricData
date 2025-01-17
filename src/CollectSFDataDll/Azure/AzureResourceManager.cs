﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using CollectSFData.Common;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace CollectSFData.Azure
{
    public class AzureResourceManager : Constants
    {
        private string _commonTenantId = "common";
        private IConfidentialClientApplication _confidentialClientApp;
        private List<string> _defaultScope = new List<string>() { ".default" };
        private string _getSubscriptionRestUri = "https://management.azure.com/subscriptions/{subscriptionId}?api-version=2016-06-01";
        private Http _httpClient = Http.ClientFactory();
        private Instance _instance = Instance.Singleton();
        private string _listSubscriptionsRestUri = "https://management.azure.com/subscriptions?api-version=2016-06-01";
        private IPublicClientApplication _publicClientApp;
        private string _resource;
        private Timer _timer;
        private DateTimeOffset _tokenExpirationHalfLife;
        private string _wellKnownClientId = "1950a258-227b-4e31-a9cf-717495945fc2";

        public delegate void MsalDeviceCodeHandler(DeviceCodeResult arg);

        public delegate void MsalHandler(LogLevel level, string message, bool containsPII);

        public static event MsalDeviceCodeHandler MsalDeviceCode;

        public static event MsalHandler MsalMessage;

        public AuthenticationResult AuthenticationResult { get; private set; }

        public string BearerToken { get; private set; }

        private ConfigurationOptions Config => _instance.Config;

        public bool IsAuthenticated { get; private set; }

        public List<string> Scopes { get; set; } = new List<string>();

        public SubscriptionRecordResult[] Subscriptions { get; private set; } = new SubscriptionRecordResult[] { };

        public AzureResourceManager()
        {
            Log.Info($"enter: token cache path: {TokenCacheHelper.CacheFilePath}");
        }

        public bool Authenticate(bool throwOnError = false, string resource = ManagementAzureCom)
        {
            Exception ex = new Exception();
            Log.Debug("azure ad:enter");
            _resource = resource;

            if (_tokenExpirationHalfLife > DateTime.Now)
            {
                Log.Debug("token still valid");
                return false;
            }
            else if (!IsAuthenticated)
            {
                Log.Info("authenticating to azure", ConsoleColor.Green);
            }
            else
            {
                Log.Warning($"refreshing aad token. token expiration half life: {_tokenExpirationHalfLife}");
            }

            try
            {
                if (string.IsNullOrEmpty(Config.AzureTenantId))
                {
                    Config.AzureTenantId = _commonTenantId;
                }

                CreateClient(false, false, resource);
                return SetToken();
            }
            catch (MsalClientException e)
            {
                Log.Warning("MsalClientException");
                ex = e;

                if (CreateClient(true, true, resource))
                {
                    return SetToken();
                }
            }
            catch (MsalUiRequiredException e)
            {
                Log.Warning("MsalUiRequiredException");
                ex = e;

                try
                {
                    if (CreateClient(true, false, resource))
                    {
                        return SetToken();
                    }
                }
                catch (AggregateException ae)
                {
                    Log.Warning($"AggregateException");

                    if (ae.GetBaseException() is MsalClientException)
                    {
                        Log.Warning($"innerexception:MsalClientException");
                        if (CreateClient(true, true, resource))
                        {
                            return SetToken();
                        }
                    }

                    Log.Exception($"AggregateException:{ae}");
                }
            }
            catch (AggregateException e)
            {
                Log.Warning($"AggregateException");
                ex = e;

                if (e.GetBaseException() is MsalClientException)
                {
                    Log.Warning($"innerexception:MsalClientException");

                    if (CreateClient(true, true, resource))
                    {
                        return SetToken();
                    }
                }
            }
            catch (Exception e)
            {
                Log.Exception($"{e}");
            }

            IsAuthenticated = false;
            Log.Last($"Authentication exception:", ConsoleColor.Yellow, null, ex);

            if (throwOnError)
            {
                throw ex;
            }

            return false;
        }

        public bool CheckResource(string resourceId)
        {
            string uri = $"{ManagementAzureCom}{resourceId}?{ArmApiVersion}";

            if (_httpClient.SendRequest(uri: uri, authToken: BearerToken, httpMethod: HttpMethod.Head))
            {
                return _httpClient.StatusCode == System.Net.HttpStatusCode.NoContent;
            }

            return false;
        }

        public bool CreateClient(bool prompt, bool deviceLogin = false, string resource = "")
        {
            if (Config.IsClientIdConfigured() & prompt)
            {
                return false;
            }
            else if (Config.IsClientIdConfigured() & !prompt)
            {
                if (!string.IsNullOrEmpty(Config.AzureClientCertificate))
                {
                    CreateConfidentialClient(resource, Config.AzureClientCertificate);
                }
                else
                {
                    CreateConfidentialClient(resource);
                }
                return true;
            }
            else
            {
                CreatePublicClient(prompt, deviceLogin);
                return true;
            }
        }

        public void CreateConfidentialClient(string resource, string clientCertificate)
        {
            X509Certificate2 certificate = ReadCertificate(clientCertificate);
            _confidentialClientApp = ConfidentialClientApplicationBuilder
                .CreateWithApplicationOptions(new ConfidentialClientApplicationOptions
                {
                    ClientId = Config.AzureClientId,
                    RedirectUri = resource,
                    TenantId = Config.AzureTenantId,
                    ClientName = Config.AzureClientId
                })
                .WithAuthority(AzureCloudInstance.AzurePublic, Config.AzureTenantId)
                .WithCertificate(certificate)
                .Build();
            AddClientScopes();
        }
        public void CreateConfidentialClient(string resource)
        {
            Log.Info($"enter: {resource}");
            // no prompt with clientid and secret
            _confidentialClientApp = ConfidentialClientApplicationBuilder
               .CreateWithApplicationOptions(new ConfidentialClientApplicationOptions
               {
                   ClientId = Config.AzureClientId,
                   RedirectUri = resource,
                   ClientSecret = Config.AzureClientSecret,
                   TenantId = Config.AzureTenantId,
                   ClientName = Config.AzureClientId
               })
               .WithAuthority(AzureCloudInstance.AzurePublic, Config.AzureTenantId)
               .WithLogging(MsalLoggerCallback, LogLevel.Verbose, true, true)
               .Build();

            AddClientScopes();
        }

        private void AddClientScopes()
        {
            if (Scopes.Count < 1)
            {
                Scopes = _defaultScope;
            }

            if (_instance.IsWindows)
            {
                TokenCacheHelper.EnableSerialization(_confidentialClientApp.AppTokenCache);
            }

            foreach (string scope in Scopes)
            {
                AuthenticationResult = _confidentialClientApp
                    .AcquireTokenForClient(new List<string>() { scope })
                    .ExecuteAsync().Result;
                Log.Debug($"scope authentication result:", AuthenticationResult);
            }
        }

        public bool CreatePublicClient(bool prompt, bool deviceLogin = false)
        {
            Log.Info($"enter: {prompt} {deviceLogin}");
            _publicClientApp = PublicClientApplicationBuilder
                .Create(_wellKnownClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, Config.AzureTenantId)
                .WithLogging(MsalLoggerCallback, LogLevel.Verbose, true, true)
                .WithDefaultRedirectUri()
                .Build();

            if (_instance.IsWindows)
            {
                TokenCacheHelper.EnableSerialization(_publicClientApp.UserTokenCache);
            }

            if (prompt)
            {
                if (deviceLogin)
                {
                    AuthenticationResult = _publicClientApp
                         .AcquireTokenWithDeviceCode(_defaultScope, MsalDeviceCodeCallback)
                         .ExecuteAsync().Result;
                }
                else
                {
                    AuthenticationResult = _publicClientApp
                        .AcquireTokenInteractive(_defaultScope)
                        .ExecuteAsync().Result;
                }
            }
            else
            {
                AuthenticationResult = _publicClientApp
                    .AcquireTokenSilent(_defaultScope, _publicClientApp.GetAccountsAsync().Result.FirstOrDefault())
                    .ExecuteAsync().Result;
            }

            if (Scopes.Count > 0)
            {
                Log.Info($"adding scopes {Scopes.Count}");
                AuthenticationResult = _publicClientApp
                    .AcquireTokenSilent(Scopes, _publicClientApp.GetAccountsAsync().Result.FirstOrDefault())
                    .ExecuteAsync().Result;
            }

            return true;
        }

        public bool CreateResourceGroup(string resourceId, string location)
        {
            Log.Info($"Checking resource group: {resourceId}");

            if (!CheckResource(resourceId))
            {
                Log.Warning($"creating resourcegroup {resourceId}");
                string uri = $"{ManagementAzureCom}{resourceId}?{ArmApiVersion}";
                JObject jBody = new JObject()
                {
                   new JProperty("location", location)
                };

                ProvisionResource(resourceId, jBody.ToString());
                return _httpClient.Success;
            }

            Log.Info($"resourcegroup exists {resourceId}");
            return true;
        }

        public Task MsalDeviceCodeCallback(DeviceCodeResult arg)
        {
            Log.Highlight($"device code info:", arg);

            MsalDeviceCodeHandler deviceCodeMessage = MsalDeviceCode;
            deviceCodeMessage?.Invoke(arg);

            return Task.FromResult(0);
        }

        public void MsalLoggerCallback(LogLevel level, string message, bool containsPII)
        {
            Log.Debug($"{level} {message.Replace(" [", "\r\n [")}");
            MsalHandler logMessage = MsalMessage;
            logMessage?.Invoke(level, message, containsPII);
        }

        public bool PopulateSubscriptions()
        {
            bool response = false;

            if (Subscriptions.Length == 0 && !string.IsNullOrEmpty(Config.AzureSubscriptionId))
            {
                response = _httpClient.SendRequest(_getSubscriptionRestUri, BearerToken
                    .Replace("{subscriptionId}", Config.AzureSubscriptionId));

                Subscriptions = new SubscriptionRecordResult[1]
                {
                    JsonConvert.DeserializeObject<SubscriptionRecordResult>(_httpClient.ResponseStreamString)
                };
            }
            else
            {
                response = _httpClient.SendRequest(_listSubscriptionsRestUri, BearerToken);
                Subscriptions = (JsonConvert.DeserializeObject<SubscriptionRecordResults>(_httpClient.ResponseStreamString)).value
                    .Where(x => x.state.ToLower() == "enabled").ToArray();
            }

            return response;
        }

        public Http ProvisionResource(string resourceId, string body = "", string apiVersion = ArmApiVersion)
        {
            Log.Info("enter");
            string uri = $"{ManagementAzureCom}{resourceId}?{apiVersion}";

            if (_httpClient.SendRequest(uri: uri, authToken: BearerToken, jsonBody: body, httpMethod: HttpMethod.Put))
            {
                int count = 0;

                // wait for state
                while (count < RetryCount)
                {
                    bool response = _httpClient.SendRequest(uri: uri, authToken: BearerToken);
                    GenericResourceResult result = JsonConvert.DeserializeObject<GenericResourceResult>(_httpClient.ResponseStreamString);

                    if (result.properties.provisioningState.ToLower() == "succeeded")
                    {
                        Log.Info($"resource provisioned {resourceId}", ConsoleColor.Green);
                        return _httpClient;
                    }

                    count++;
                    Log.Info($"requery count: {count} of {RetryCount} response: {response}");
                    Thread.Sleep(ThreadSleepMs10000);
                }
            }

            Log.Error($"unable to provision {resourceId}");
            _httpClient.Success = false;
            return _httpClient;
        }

        public void Reauthenticate(object state = null)
        {
            Log.Highlight("azure ad reauthenticate");
            Authenticate(true, _resource);
        }

        private X509Certificate2 ReadCertificate(string certificateId)
        {
            X509Certificate2 certificate = null;
            certificate = ReadCertificateFromStore(certificateId);

            if (certificate == null)
            {
                certificate = ReadCertificateFromStore(certificateId, StoreName.My, StoreLocation.LocalMachine);
            }
            return certificate;
        }

        private static X509Certificate2 ReadCertificateFromStore(string certificateId, StoreName storeName = StoreName.My, StoreLocation storeLocation = StoreLocation.CurrentUser)
        {
            X509Certificate2 certificate = null;

            using (X509Store store = new X509Store(storeName, storeLocation))
            {
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection certCollection = store.Certificates;

                // Find unexpired certificates.
                X509Certificate2Collection currentCerts = certCollection.Find(X509FindType.FindByTimeValid, DateTime.Now, false);

                // From the collection of unexpired certificates, find the ones with the correct name.
                X509Certificate2Collection signingCert = currentCerts.Find(X509FindType.FindBySubjectName, certificateId, false);

                // Return the first certificate in the collection, has the right name and is current.
                certificate = signingCert.OfType<X509Certificate2>().OrderByDescending(c => c.NotBefore).FirstOrDefault();
            }
            return certificate;
        }

        public Http SendRequest(string uri, HttpMethod method = null, string body = "")
        {
            method = method ?? _httpClient.Method;
            _httpClient.SendRequest(uri: uri, authToken: BearerToken, jsonBody: body, httpMethod: method);
            return _httpClient;
        }

        public bool SetToken()
        {
            if (AuthenticationResult?.AccessToken != null)
            {
                BearerToken = AuthenticationResult.AccessToken;
                long tickDiff = ((AuthenticationResult.ExpiresOn.ToLocalTime().Ticks - DateTime.Now.Ticks) / 2) + DateTime.Now.Ticks;
                _tokenExpirationHalfLife = new DateTimeOffset(new DateTime(tickDiff));

                Log.Debug($"authentication result:", AuthenticationResult);
                Log.Highlight($"aad token expiration: {AuthenticationResult.ExpiresOn.ToLocalTime()}");
                Log.Highlight($"aad token half life expiration: {_tokenExpirationHalfLife}");

                _timer = new Timer(Reauthenticate, null, Convert.ToInt32((_tokenExpirationHalfLife - DateTime.Now).TotalMilliseconds), Timeout.Infinite);
                IsAuthenticated = true;

                return true;
            }
            else
            {
                Log.Info($"authentication result:", ConsoleColor.Green, null, AuthenticationResult);
                return false;
            }
        }
    }
}