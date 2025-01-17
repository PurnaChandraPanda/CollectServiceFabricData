﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using CollectSFData.Azure;
using CollectSFData.DataFile;
using Microsoft.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace CollectSFData.Common
{
    public class ConfigurationOptions : ConfigurationProperties
    {
        private static readonly CommandLineArguments _cmdLineArgs = new CommandLineArguments();
        private static string[] _commandlineArguments = new string[0];
        private static bool _cmdLineExecuted;
        private static ConfigurationOptions _defaultConfig;
        private string _tempPath;
        private readonly string _workDir = "csfd";


        public new string EndTimeStamp
        {
            get => base.EndTimeStamp;
            set
            {
                EndTimeUtc = ConvertToUtcTime(value);
                base.EndTimeStamp = ConvertToUtcTimeString(value);
            }
        }

        public string ExePath {get;} = $"{Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)}\\{DefaultOptionsFile}";

        public FileTypesEnum FileType { get; private set; }

        public new string GatherType
        {
            get => base.GatherType;
            set
            {
                FileTypesEnum fileType = ConvertFileType(value);

                if (fileType == FileTypesEnum.unknown)
                {
                    Log.Warning($"GatherType unknown: {value}");
                }

                FileType = fileType;
                base.GatherType = FileType.ToString();
            }
        }

        public bool IsValid { get; set; }

        public new int LogDebug
        {
            get => base.LogDebug;
            set
            {
                base.LogDebug = value;
                Log.LogDebug = value;
            }
        }

        public bool NeedsValidation {get;set;} = true;

        public new string StartTimeStamp
        {
            get => base.StartTimeStamp;
            set
            {
                StartTimeUtc = ConvertToUtcTime(value);
                base.StartTimeStamp = ConvertToUtcTimeString(value);
            }
        }

        public new int Threads
        {
            get => base.Threads < 1 ? Environment.ProcessorCount : base.Threads;
            set => base.Threads = value < 1 ? Environment.ProcessorCount : value;
        }

        public string Version { get;} = $"{Process.GetCurrentProcess().MainModule?.FileVersionInfo.FileVersion}";

        private bool _defaultConfigLoaded => _defaultConfig != null;

        static ConfigurationOptions()
        {
            _cmdLineArgs.InitFromCmdLine();
        }

        public ConfigurationOptions() : this(new string[0])
        {
        }

        public ConfigurationOptions(string[] commandlineArguments, bool validate = false)
        {
            if (commandlineArguments.Any())
            {
                _commandlineArguments = commandlineArguments;
            }

            _tempPath = FileManager.NormalizePath(Path.GetTempPath() + _workDir);

            DateTimeOffset defaultOffset = DateTimeOffset.Now;
            StartTimeUtc = defaultOffset.UtcDateTime.AddHours(DefaultStartTimeHours);
            StartTimeStamp = defaultOffset.AddHours(DefaultStartTimeHours).ToString(DefaultDatePattern);
            EndTimeUtc = defaultOffset.UtcDateTime;
            EndTimeStamp = defaultOffset.ToString(DefaultDatePattern);
            LoadDefaultConfig();

            if (validate)
            {
                Validate();
            }
        }

        public void CheckReleaseVersion()
        {
            string response = $"\r\n\tlocal running version: {Version}";
            Http http = Http.ClientFactory();
            http.DisplayResponse = false;
            http.DisplayError = false;

            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers.Add("User-Agent", $"{AppDomain.CurrentDomain.FriendlyName}");

            try
            {
                if (http.SendRequest(uri: CodeLatestRelease, headers: headers, httpMethod: HttpMethod.Head)
                     && http.SendRequest(uri: CodeLatestRelease, headers: headers))
                {
                    JToken downloadUrl = http.ResponseStreamJson.SelectToken("assets[0].browser_download_url");
                    JToken downloadVersion = http.ResponseStreamJson.SelectToken("tag_name");
                    JToken body = http.ResponseStreamJson.SelectToken("body");

                    if (new Version(Version) <= new Version(downloadVersion.ToString().TrimStart('v')))
                    {
                        response += $"\r\n\tlatest download release version: {downloadVersion.ToString()}";
                        response += $"\r\n\trelease notes: \r\n\t\t{body.ToString().Replace("\r\n", "\r\n\t\t")}";
                        response += $"\r\n\tlatest download release url: {downloadUrl.ToString()}";
                    }
                    else
                    {
                        response += $"\r\n\tlocal running version is latest.";
                    }
                }

                Log.Last(response);
            }
            catch
            {
                Log.Last(response);
            }
        }

        public ConfigurationOptions Clone()
        {
            return (ConfigurationOptions)MemberwiseClone();
        }

        public DateTime ConvertToUtcTime(string timeString)
        {
            DateTimeOffset dateTimeOffset;

            if (DateTimeOffset.TryParse(timeString, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTimeOffset))
            {
                Log.Debug($"TimeStamp valid format:input:'{timeString}'");
                return dateTimeOffset.UtcDateTime;
            }

            Log.Error($"TimeStamp invalid format:input:'{timeString}' but expecting pattern:'{DefaultDatePattern}' example:'{DateTime.Now.ToString(DefaultDatePattern)}'");
            return DateTime.MinValue;
        }

        public string ConvertToUtcTimeString(string timeString)
        {
            DateTime dateTime = DateTime.MinValue;

            if (string.IsNullOrEmpty(timeString))
            {
                Log.Warning("empty time string");
            }
            else
            {
                dateTime = ConvertToUtcTime(timeString);
                if (dateTime != DateTime.MinValue)
                {
                    timeString = dateTime.ToString(DefaultDatePattern);
                }
            }

            Log.Debug($"returning:time string:'{timeString}'");
            return timeString;
        }

        public void DisplayStatus()
        {
            Log.Min($"      Gathering: {FileType.ToString()}", ConsoleColor.White);
            Log.Min($"         Source: {(SasEndpointInfo?.StorageAccountName ?? CacheLocation)}", ConsoleColor.White);
            Log.Min($"     Start Time: {StartTimeStamp}", ConsoleColor.White);
            Log.Min($"            UTC: {StartTimeUtc.ToString(DefaultDatePattern)}", ConsoleColor.White);
            Log.Min($"          Local: {StartTimeUtc.ToLocalTime().ToString(DefaultDatePattern)}", ConsoleColor.White);
            Log.Min($"       End Time: {EndTimeStamp}", ConsoleColor.White);
            Log.Min($"            UTC: {EndTimeUtc.ToString(DefaultDatePattern)}", ConsoleColor.White);
            Log.Min($"          Local: {EndTimeUtc.ToLocalTime().ToString(DefaultDatePattern)}", ConsoleColor.White);
            Log.Min($"        Threads: {Threads}", ConsoleColor.White);
            Log.Min($"  CacheLocation: {CacheLocation}", ConsoleColor.White);
            Log.Min($"     NodeFilter: {NodeFilter}", ConsoleColor.White);
            Log.Min($"      UriFilter: {UriFilter}", ConsoleColor.White);
            Log.Min($"ContainerFilter: {ContainerFilter}", ConsoleColor.White);

            if (IsKustoConfigured())
            {
                Log.Min($"KustoTable: {KustoTable}", ConsoleColor.White);
            }

            if (IsLogAnalyticsConfigured())
            {
                Log.Min($"LogAnalyticsName: {LogAnalyticsName}_CL", ConsoleColor.White);

                if (LogAnalyticsRecreate | LogAnalyticsCreate)
                {
                    Log.Warning($"new log analytics information: save to config file!");
                    Log.Min($"LogAnalyticsId: {LogAnalyticsId}", ConsoleColor.Yellow);
                    Log.Min($"LogAnalyticsKey: {LogAnalyticsKey}", ConsoleColor.Yellow);
                    Log.Min($"LogAnalyticsWorkspaceName: {LogAnalyticsWorkspaceName}", ConsoleColor.Yellow);
                    Log.Min($"AzureSubscriptionId: {AzureSubscriptionId}", ConsoleColor.Yellow);
                    Log.Min($"AzureResourceGroup: {AzureResourceGroup}", ConsoleColor.Yellow);
                    Log.Min($"AzureResourceGroupLocation: {AzureResourceGroupLocation}", ConsoleColor.Yellow);
                }
            }
        }

        public ConfigurationOptions GetDefaultConfig()
        {
            LoadDefaultConfig();
            Log.Debug($"exit:", _defaultConfig);
            return _defaultConfig;
        }

        public bool IsCacheLocationPreConfigured()
        {
            // saving config file with no options will set cache location to %temp% by default
            // collectsfdata.exe -save file.json
            return !(string.IsNullOrEmpty(CacheLocation) | (CacheLocation == _tempPath));
        }

        public bool IsClientIdConfigured()
        {
            return AzureClientId?.Length > 0 & (AzureClientSecret?.Length > 0 | AzureClientCertificate?.Length > 0) & AzureTenantId?.Length > 0;
        }

        public bool IsKustoConfigured()
        {
            return (!FileType.Equals(FileTypesEnum.any) & !string.IsNullOrEmpty(KustoCluster) & !string.IsNullOrEmpty(KustoTable));
        }

        public bool IsKustoPurgeRequested()
        {
            return !string.IsNullOrEmpty(KustoPurge);
        }

        public bool IsLogAnalyticsConfigured()
        {
            return (!string.IsNullOrEmpty(LogAnalyticsId) | LogAnalyticsCreate)
                & (!string.IsNullOrEmpty(LogAnalyticsKey) | LogAnalyticsCreate)
                & !string.IsNullOrEmpty(LogAnalyticsName);
        }

        public bool IsLogAnalyticsPurgeRequested()
        {
            return !string.IsNullOrEmpty(LogAnalyticsPurge);
        }

        public void MergeConfig(string optionsFile)
        {
            JObject fileOptions = ReadConfigFile(optionsFile);
            MergeConfig(fileOptions);
        }

        public void MergeConfig(ConfigurationProperties configurationProperties)
        {
            JObject options = JObject.FromObject(configurationProperties);
            MergeConfig(options);
        }

        public void MergeConfig(ConfigurationOptions configurationOptions)
        {
            JObject options = JObject.FromObject(configurationOptions);
            MergeConfig(options);
        }

        public void MergeConfig(JObject fileOptions)
        {
            object instanceValue = null;

            if (fileOptions == null || !fileOptions.HasValues)
            {
                Log.Error($"empty options:", fileOptions);
                throw new ArgumentException();
            }

            PropertyInfo[] instanceProperties = InstanceProperties();

            foreach (KeyValuePair<string, JToken> fileOption in fileOptions)
            {
                Log.Debug($"JObject config option:{fileOption.Key}");

                if (!instanceProperties.Any(x => Regex.IsMatch(x.Name, fileOption.Key.Replace("$", ""), RegexOptions.IgnoreCase)))
                {
                    Log.Error($"unknown config file option:{fileOption.Key}");
                }
            }

            foreach (PropertyInfo instanceProperty in instanceProperties)
            {
                if (!fileOptions.ToObject<Dictionary<string, JToken>>().Any(x => Regex.IsMatch(x.Key, instanceProperty.Name, RegexOptions.IgnoreCase)))
                {
                    Log.Debug($"instance option not found in file:{instanceProperty.Name}");
                    continue;
                }

                JToken token = fileOptions.ToObject<Dictionary<string, JToken>>()
                    .First(x => Regex.IsMatch(x.Key, instanceProperty.Name, RegexOptions.IgnoreCase)).Value;
                Log.Debug($"token:{token.Type}");

                switch (token.Type)
                {
                    case JTokenType.Array:
                        instanceValue = token.Values<string>().ToArray();
                        break;

                    case JTokenType.Null:
                        instanceValue = null;
                        break;

                    case JTokenType.Boolean:
                        instanceValue = token.Value<bool>();
                        break;

                    case JTokenType.Integer:
                        instanceValue = token.Value<int>();
                        break;

                    case JTokenType.String:
                        instanceValue = token.Value<string>();
                        break;

                    case JTokenType.Uri:
                        instanceValue = token.Value<string>();
                        break;

                    case JTokenType.Date:
                        // issue with date and datetimeoffset
                        if (instanceProperty.PropertyType.Equals(typeof(string)))
                        {
                            instanceValue = token.Value<string>();
                        }
                        else
                        {
                            instanceValue = token.ToObject<DateTimeOffset>();
                        }

                        break;

                    case JTokenType.Guid:
                        instanceValue = token.Value<string>();
                        break;

                    default:
                        Log.Debug($"jtoken type unknown:{token}");
                        continue;
                }

                SetPropertyValue(instanceProperty, instanceValue);
            }

            SetDefaultConfig(Clone());
        }

        private bool ProcessArguments()
        {
            try
            {
                if (_cmdLineExecuted)
                {
                    return true;
                }
                else if (_commandlineArguments.Length == 0 && !_defaultConfigLoaded && GatherType == FileTypesEnum.unknown.ToString())
                {
                    Log.Last(_cmdLineArgs.CmdLineApp.GetHelpText());
                    Log.Last("error: no configuration provided");
                    return false;
                }

                if (_commandlineArguments.Length == 1)
                {
                    // check for help and FTA
                    if (!_commandlineArguments[0].StartsWith("/?") && !_commandlineArguments[0].StartsWith("-") && _commandlineArguments[0].EndsWith(".json") && File.Exists(_commandlineArguments[0]))
                    {
                        ConfigurationFile = _commandlineArguments[0];
                        MergeConfig(ConfigurationFile);
                        Log.Info($"setting options to {DefaultOptionsFile}", ConsoleColor.Yellow);
                    }
                    else if (_commandlineArguments[0].StartsWith("/?") | _commandlineArguments[0].StartsWith("-?") | _commandlineArguments[0].StartsWith("--?"))
                    {
                        Log.Last(_cmdLineArgs.CmdLineApp.GetHelpText());
                        return false;
                    }
                }

                // check for name and value pair
                for (int i = 0; i < _commandlineArguments.Length - 1; i++)
                {
                    string name = _commandlineArguments[i];
                    string value = _commandlineArguments[++i];

                    if (!Regex.IsMatch($"{name} {value}", "-\\w+ [^-]"))
                    {
                        Log.Error($"invalid argument pair: parameter name: {name} parameter value: {value}");
                        Log.Error("all parameters are required to have a value.");
                        return false;
                    }
                }

                if (!ParseCmdLine(_commandlineArguments))
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(ConfigurationFile))
                {
                    foreach (string file in ConfigurationFile.Split(','))
                    {
                        MergeConfig(file);
                    }

                    MergeCmdLine();
                }

                EndTimeUtc = EndTimeUtc.AddHours(WarningTimeSpanMinHours);
                Log.Highlight($"adding {WarningTimeSpanMinHours * 60} minutes to EndTimeUtc to compensate for sf file upload timer. New EndTimeUtc: ({EndTimeUtc.ToString("o")})");

                if (VersionOption)
                {
                    CheckReleaseVersion();
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                Log.Exception($"{e}");
                Log.Last(_cmdLineArgs.CmdLineApp.GetHelpText());
                return false;
            }
        }

        public ConfigurationProperties PropertyClone()
        {
            return (ConfigurationProperties)base.MemberwiseClone();
        }

        public string SaveConfigFile()
        {
            if (string.IsNullOrEmpty(SaveConfiguration))
            {
                return null;
            }

            // remove options that should not be saved in configuration file
            JObject options = JObject.FromObject(this);
            options.AddFirst(new JProperty("$schema", SchemaFile));
            options.Remove("Schema");
            options.Remove("ConfigurationFile");
            options.Remove("EndTimeUtc");
            options.Remove("Examples");
            options.Remove("ExePath");
            options.Remove("FileType");
            options.Remove("IsValid");
            options.Remove("NeedsValidation");
            options.Remove("SasEndpointInfo");
            options.Remove("SaveConfiguration");
            options.Remove("StartTimeUtc");
            options.Remove("VersionOption");
            options.Remove("Version");

            if (IsKustoConfigured())
            {
                options.Property("KustoTable").Value = CleanTableName(KustoTable);
            }

            if (IsLogAnalyticsConfigured())
            {
                options.Property("LogAnalyticsName").Value = CleanTableName(LogAnalyticsName);
            }

            if (!IsCacheLocationPreConfigured())
            {
                options.Property("CacheLocation").Value = null;
            }

            Log.Info($"options results:", options);

            File.WriteAllText(SaveConfiguration, options.ToString());
            Log.Info($"configuration file saved to: {SaveConfiguration}", ConsoleColor.Green);
            return options.ToString();
        }

        public void SetDefaultConfig(ConfigurationOptions configurationOptions)
        {
            Log.Debug($"enter:", configurationOptions);
            _defaultConfig = configurationOptions;
        }

        public bool Validate()
        {
            try
            {
                bool retval = true;
                NeedsValidation = false;

                if (!ProcessArguments())
                {
                    retval = false;
                }
                else
                {
                    CheckLogFile();

                    retval &= ValidateSasKey();
                    retval &= ValidateFileType();
                    retval &= ValidateTime();

                    CheckCache();
                    retval &= ValidateSource();
                    retval &= ValidateDestination();
                    retval &= ValidateAad();

                    if (retval)
                    {
                        Log.Info($"options:", Clone());
                        DisplayStatus();
                    }
                    else
                    {
                        Log.Warning($"review console output above for errors and warnings. refer to {CodeRepository} for additional information.");
                    }
                }

                SaveConfigFile();
                return IsValid = retval;
            }
            catch (Exception e)
            {
                Log.Exception($"validate:exception:{e}");
                return IsValid = false;
            }
        }

        public bool ValidateAad()
        {
            bool retval = true;
            bool needsAad = IsKustoConfigured() | IsKustoPurgeRequested() | IsLogAnalyticsConfigured();
            needsAad |= LogAnalyticsCreate | LogAnalyticsRecreate | IsLogAnalyticsPurgeRequested();

            if (needsAad | IsClientIdConfigured())
            {
                AzureResourceManager arm = new AzureResourceManager();
                retval = arm.Authenticate();

                // LA workspace commands require subscription id. if not specified and tenant has more than one, fail
                if ((LogAnalyticsCreate | LogAnalyticsRecreate) && string.IsNullOrEmpty(AzureSubscriptionId))
                {
                    if (arm.PopulateSubscriptions() && arm.Subscriptions.Length != 1)
                    {
                        Log.Error($"this configuration requires AzureSubscriptionId to be configured", arm.Subscriptions);
                        retval = false;
                    }
                    else
                    {
                        AzureSubscriptionId = arm.Subscriptions[0].id;
                        Log.Info($"AzureSubscriptionId set to {AzureSubscriptionId}");
                    }
                }
            }

            return retval;
        }

        public bool ValidateDestination()
        {
            bool retval = true;

            if (IsKustoConfigured() | IsKustoPurgeRequested())
            {
                KustoTable = CleanTableName(KustoTable, true);
                Log.Info($"adding prefix to KustoTable: {KustoTable}");

                if (IsKustoPurgeRequested())
                {
                    retval = IsKustoConfigured();
                }

                if (!Regex.IsMatch(KustoCluster, KustoUrlPattern))
                {
                    string errMessage = $"invalid kusto url. should match pattern {KustoUrlPattern}\r\nexample: https://ingest-{{kustocluster}}.{{optional location}}.kusto.windows.net/{{kustodatabase}}";
                    Log.Error(errMessage);
                    retval = false;
                }
            }

            if (LogAnalyticsCreate & !IsLogAnalyticsConfigured())
            {
                if (string.IsNullOrEmpty(AzureResourceGroup) | string.IsNullOrEmpty(AzureResourceGroupLocation) | string.IsNullOrEmpty(LogAnalyticsWorkspaceName))
                {
                    Log.Error("LogAnalyticsWorkspaceName, AzureSubscriptionId, AzureResourceGroup, and AzureResourceGroupLocation are required for LogAnalyticsCreate");
                    retval = false;
                }
            }

            if (IsLogAnalyticsConfigured() | IsLogAnalyticsPurgeRequested())
            {
                LogAnalyticsName = CleanTableName(LogAnalyticsName, true);
                Log.Info($"adding prefix to logAnalyticsName: {LogAnalyticsName}");

                if (IsLogAnalyticsConfigured() & Unique & string.IsNullOrEmpty(AzureSubscriptionId))
                {
                    Log.Error($"log analytics and 'Unique' require 'AzureSubscriptionId'. supply AzureSubscriptionId or set Unique to false.");
                    retval = false;
                }

                if (LogAnalyticsCreate & LogAnalyticsRecreate)
                {
                    Log.Error($"log analytics create and log analytics recreate *cannot* both be enabled. remove configuration for one");
                    retval = false;
                }

                if (IsLogAnalyticsPurgeRequested())
                {
                    if (string.IsNullOrEmpty(LogAnalyticsId) | string.IsNullOrEmpty(LogAnalyticsKey) | string.IsNullOrEmpty(LogAnalyticsName))
                    {
                        Log.Error("LogAnalyticsId, LogAnalyticsKey, and LogAnalyticsName are required for LogAnalyticsPurge");
                        retval = false;
                    }
                }

                if (LogAnalyticsRecreate)
                {
                    if (string.IsNullOrEmpty(LogAnalyticsId) | string.IsNullOrEmpty(LogAnalyticsKey) | string.IsNullOrEmpty(LogAnalyticsName))
                    {
                        Log.Error("LogAnalyticsId, LogAnalyticsKey, and LogAnalyticsName are required for LogAnalyticsRecreate");
                        retval = false;
                    }
                }
            }

            if (IsKustoConfigured() & IsLogAnalyticsConfigured())
            {
                Log.Error($"kusto and log analytics *cannot* both be enabled. remove configuration for one");
                retval = false;
            }

            if (!IsKustoConfigured() & !IsLogAnalyticsConfigured() & !IsCacheLocationPreConfigured())
            {
                Log.Error($"kusto or log analytics or cacheLocation must be configured for file destination.");
                retval = false;
            }

            if (!IsKustoConfigured() & !IsLogAnalyticsConfigured() & UseMemoryStream)
            {
                Log.Error($"kusto or log analytics must be configured for UseMemoryStream.");
                retval = false;
            }

            return retval;
        }

        public bool ValidateFileType()
        {
            bool retval = true;

            if (FileType == FileTypesEnum.unknown)
            {
                Log.Warning($"invalid -type|--gatherType argument, value can be:", Enum.GetNames(typeof(FileTypesEnum)).Skip(1));
                retval = false;
            }

            if (FileType == FileTypesEnum.any && (UseMemoryStream | DeleteCache))
            {
                Log.Warning($"setting UseMemoryStream and DeleteCache to false for FileType 'any'");
                UseMemoryStream = false;
                DeleteCache = false;
            }

            if (FileType != FileTypesEnum.unknown && FileType != FileTypesEnum.trace && KustoUseBlobAsSource)
            {
                Log.Warning($"setting KustoUseBlobAsSource to false for FileType: {FileType}");
                KustoUseBlobAsSource = false;
            }

            if (FileUris.Length > 0 && KustoUseBlobAsSource)
            {
                Log.Warning($"setting KustoUseBlobAsSource to false for FileUris");
                KustoUseBlobAsSource = false;
            }

            return retval;
        }

        public bool ValidateSasKey()
        {
            if (!string.IsNullOrEmpty(SasKey))
            {
                SasEndpointInfo = new SasEndpoints(SasKey);
                return SasEndpointInfo.IsValid();
            }

            return true;
        }

        public bool ValidateSource()
        {
            bool retval = true;

            if (!SasEndpointInfo.IsPopulated() & !IsCacheLocationPreConfigured() & FileUris.Length < 1)
            {
                Log.Error($"sasKey, fileUris, or cacheLocation should be populated as file source.");
                retval = false;
            }

            return retval;
        }

        public bool ValidateTime()
        {
            Log.Info("enter");
            bool retval = true;

            if (string.IsNullOrEmpty(StartTimeStamp) != string.IsNullOrEmpty(EndTimeStamp))
            {
                Log.Error("supply start and end time");
                retval = false;
            }

            if (!string.IsNullOrEmpty(StartTimeStamp) & !string.IsNullOrEmpty(EndTimeStamp))
            {
                if (ConvertToUtcTime(StartTimeStamp) == DateTime.MinValue | ConvertToUtcTime(EndTimeStamp) == DateTime.MinValue)
                {
                    retval = false;
                }
                else if (EndTimeUtc <= StartTimeUtc)
                {
                    Log.Error("supply start time less than end time");
                    retval = false;
                }
                else if ((EndTimeUtc - StartTimeUtc).TotalHours > WarningTimeSpanHours)
                {
                    Log.Warning($"current time range hours ({(EndTimeUtc - StartTimeUtc).TotalHours}) over maximum recommended time range hours ({WarningTimeSpanHours})");
                }
                else if ((EndTimeUtc - StartTimeUtc).TotalHours < WarningTimeSpanMinHours)
                {
                    Log.Warning($"current time range hours ({(EndTimeUtc - StartTimeUtc).TotalHours}) below minimum recommended time range hours ({WarningTimeSpanMinHours})");
                }
            }

            Log.Info($"exit:return:{retval}");
            return retval;
        }

        private static FileTypesEnum ConvertFileType(string fileTypeString)
        {
            if (string.IsNullOrEmpty(fileTypeString) || !Enum.TryParse(fileTypeString.ToLower(), out FileTypesEnum fileType))
            {
                return FileTypesEnum.unknown;
            }

            return fileType;
        }

        private void CheckCache()
        {
            if (!IsCacheLocationPreConfigured())
            {
                CacheLocation = _tempPath;
            }

            CacheLocation = FileManager.NormalizePath(CacheLocation);

            if (!Directory.Exists(CacheLocation))
            {
                Directory.CreateDirectory(CacheLocation);
            }
            else if (Directory.Exists(CacheLocation)
                & Directory.GetFileSystemEntries(CacheLocation).Length > 0
                & DeleteCache
                & SasEndpointInfo.IsPopulated())
            {
                // add working dir to outputlocation so it can be deleted
                string workDirPath = $"{CacheLocation}{Path.DirectorySeparatorChar}{Path.GetFileName(Path.GetTempFileName())}";
                Log.Warning($"outputlocation not empty and DeleteCache is enabled, creating work subdir {workDirPath}");

                if (!Directory.Exists(workDirPath))
                {
                    Directory.CreateDirectory(workDirPath);
                }

                Log.Info($"setting output location to: {workDirPath}");
                CacheLocation = FileManager.NormalizePath(workDirPath);
            }

            if (!UseMemoryStream && !CacheLocation.StartsWith("\\\\"))
            {
                DriveInfo drive = DriveInfo.GetDrives().FirstOrDefault(x => String.Equals(x.Name, Path.GetPathRoot(CacheLocation), StringComparison.OrdinalIgnoreCase));
                if (drive != null && drive.AvailableFreeSpace < ((long)1024 * 1024 * 1024 * 100))
                {
                    Log.Warning($"available free space in {CacheLocation} is less than 100 GB");
                }
            }

            if (DeleteCache & !SasEndpointInfo.IsPopulated())
            {
                Log.Warning($"setting 'DeleteCache' is set to true but no sas information provided.\r\nfiles will be deleted at exit!\r\nctrl-c now if this incorrect.");
                Thread.Sleep(ThreadSleepMsWarning);
            }
        }

        private void CheckLogFile()
        {
            if (!string.IsNullOrEmpty(LogFile))
            {
                Log.LogFile = FileManager.NormalizePath(LogFile);
                Log.Info($"setting output log file to: {LogFile}");
            }
        }

        private string CleanTableName(string tableName, bool withGatherType = false)
        {
            if (withGatherType)
            {
                return FileType + "_" + Regex.Replace(tableName, $"^({FileType}_)", "", RegexOptions.IgnoreCase);
            }

            return Regex.Replace(tableName, $"^({FileType}_)", "", RegexOptions.IgnoreCase);
        }

        private PropertyInfo[] InstanceProperties()
        {
            return GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        }

        private bool LoadDefaultConfig()
        {
            if (_defaultConfig == null)
            {
                if (File.Exists(DefaultOptionsFile))
                {
                    MergeConfig(DefaultOptionsFile);
                    return true;
                }
                else if (File.Exists(ExePath))
                {
                    MergeConfig(ExePath);
                    return true;
                }

                return false;
            }
            else
            {
                MergeConfig(_defaultConfig);
            }

            return true;
        }

        private int MergeCmdLine()
        {
            try
            {
                PropertyInfo[] instanceProperties = InstanceProperties();
                PropertyInfo[] argumentProperties = _cmdLineArgs.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(x => x.PropertyType == typeof(CommandOption)).ToArray();

                foreach (PropertyInfo argumentProperty in argumentProperties)
                {
                    if (!instanceProperties.Any(x => x.Name.Equals(argumentProperty.Name)))
                    {
                        Log.Debug($"options / arguments properties mismatch: {argumentProperty.Name}");
                        throw new Exception($"options / arguments properties mismatch:{argumentProperty.Name}");
                    }

                    if (!((CommandOption)argumentProperty.GetValue(_cmdLineArgs)).HasValue())
                    {
                        Log.Debug($"empty / null argument value. skipping: {argumentProperty.Name}");
                        continue;
                    }

                    PropertyInfo instanceProperty = instanceProperties.First(x => x.Name.Equals(argumentProperty.Name));
                    object instanceValue = ((CommandOption)argumentProperty.GetValue(_cmdLineArgs)).Value();
                    Log.Debug($"argumentProperty: {argumentProperty.Name}");
                    Log.Debug($"instanceProperty: {instanceProperty.Name}");

                    if (instanceProperty.PropertyType == typeof(string[]))
                    {
                        SetPropertyValue(instanceProperty, instanceValue.ToString().Split(','));
                    }
                    else if (instanceProperty.PropertyType == typeof(string))
                    {
                        SetPropertyValue(instanceProperty, instanceValue.ToString());
                    }
                    else if (instanceProperty.PropertyType == typeof(int))
                    {
                        SetPropertyValue(instanceProperty, Convert.ToInt32(instanceValue));
                    }
                    else if (instanceProperty.PropertyType == typeof(bool))
                    {
                        bool value = false;

                        if (Regex.IsMatch(instanceValue.ToString(), TrueStringPattern, RegexOptions.IgnoreCase))
                        {
                            value = true;
                        }
                        else if (!Regex.IsMatch(instanceValue.ToString(), FalseStringPattern, RegexOptions.IgnoreCase))
                        {
                            string error = $"{instanceProperty.Name} bool argument values on command line should either be {TrueStringPattern} or {FalseStringPattern}";
                            throw new ArgumentException(error);
                        }

                        SetPropertyValue(instanceProperty, value);
                    }
                    else
                    {
                        Log.Error($"undefined property type:", argumentProperty);
                        throw new Exception($"undefined property type:{argumentProperty.Name}");
                    }
                }

                if (Examples)
                {
                    _cmdLineArgs.DisplayExamples();
                    return 0;
                }

                SetDefaultConfig(Clone());
                return 1;
            }
            catch (Exception e)
            {
                Log.Exception($"{e}");
                return 0;
            }
        }

        private bool ParseCmdLine(string[] args)
        {
            try
            {
                // can only be called once
                if (!_cmdLineExecuted & args.Any())
                {
                    _cmdLineExecuted = true;
                    _cmdLineArgs.CmdLineApp.OnExecute(() => MergeCmdLine());

                    if (_cmdLineArgs.CmdLineApp.Execute(args) == 0)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                Log.Exception($"{e}");
                Log.Last(_cmdLineArgs.CmdLineApp.GetHelpText());
                return false;
            }
        }

        private JObject ReadConfigFile(string configFile)
        {
            JObject options = new JObject();

            try
            {
                Log.Info($"reading {configFile}", ConsoleColor.Yellow);
                options = (JObject)JsonConvert.DeserializeObject(File.ReadAllText(configFile));
                Log.Info($"options results:", options);
                return options;
            }
            catch (Exception e)
            {
                Log.Exception($"returning default options: exception:{e}");
                return options;
            }
        }

        private void SetPropertyValue(PropertyInfo propertyInstance, object instanceValue)
        {
            object thisValue = propertyInstance.GetValue(this);
            Log.Debug($"checking:{propertyInstance.Name}:{thisValue} -> {instanceValue}");

            if ((thisValue != null && thisValue.Equals(instanceValue))
                | (thisValue == null & instanceValue == null))
            {
                Log.Debug("value same. skipping.");
                return;
            }

            if (propertyInstance.CanWrite)
            {
                try
                {
                    propertyInstance.SetValue(this, instanceValue);
                    Log.Debug($"set:{propertyInstance.Name}:{thisValue} -> {instanceValue}");
                }
                catch (Exception e)
                {
                    Log.Exception($"exception modifying:{propertyInstance.Name} {thisValue} -> {instanceValue}", e);
                }
            }
            else
            {
                Log.Debug($"property not writable:{propertyInstance.Name}");
            }
        }
    }
}