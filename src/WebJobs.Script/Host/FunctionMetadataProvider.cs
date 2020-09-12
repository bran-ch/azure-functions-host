﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Diagnostics.Extensions;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script
{
    public class FunctionMetadataProvider : IFunctionMetadataProvider
    {
        private readonly IOptionsMonitor<ScriptApplicationHostOptions> _applicationHostOptions;
        private readonly IMetricsLogger _metricsLogger;
        private readonly Dictionary<string, ICollection<string>> _functionErrors = new Dictionary<string, ICollection<string>>();
        private readonly ILogger _logger;
        private ImmutableArray<FunctionMetadata> _functions;

        public FunctionMetadataProvider(IOptionsMonitor<ScriptApplicationHostOptions> applicationHostOptions, ILogger<FunctionMetadataProvider> logger, IMetricsLogger metricsLogger)
        {
            _applicationHostOptions = applicationHostOptions;
            _metricsLogger = metricsLogger;
            _logger = logger;
        }

        public ImmutableDictionary<string, ImmutableArray<string>> FunctionErrors
           => _functionErrors.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableArray());

        public ImmutableArray<FunctionMetadata> GetFunctionMetadata(IEnumerable<RpcWorkerConfig> workerConfigs, bool forceRefresh)
        {
            if (_functions.IsDefaultOrEmpty || forceRefresh)
            {
                _logger.FunctionMetadataProviderParsingFunctions();
                Collection<FunctionMetadata> functionMetadata = ReadFunctionsMetadata(workerConfigs);
                _logger.FunctionMetadataProviderFunctionFound(functionMetadata.Count);
                _functions = functionMetadata.ToImmutableArray();
            }

            return _functions;
        }

        internal Collection<FunctionMetadata> ReadFunctionsMetadata(IEnumerable<RpcWorkerConfig> workerConfigs, IFileSystem fileSystem = null)
        {
            _functionErrors.Clear();
            fileSystem = fileSystem ?? FileUtility.Instance;
            using (_metricsLogger.LatencyEvent(MetricEventNames.ReadFunctionsMetadata))
            {
                var functions = new Collection<FunctionMetadata>();

                if (!fileSystem.Directory.Exists(_applicationHostOptions.CurrentValue.ScriptPath))
                {
                    return functions;
                }

                // read function directories
                var functionDirectories = fileSystem
                    .Directory
                    .EnumerateDirectories(_applicationHostOptions.CurrentValue.ScriptPath)
                    .Where(directory => directory != ScriptConstants.FunctionMetadataFolderName)
                    .ToImmutableArray();
                foreach (var functionDirectory in functionDirectories)
                {
                    var function = ReadFunctionMetadata(functionDirectory, ScriptConstants.FunctionMetadataFileName, fileSystem, workerConfigs);
                    if (function != null)
                    {
                        functions.Add(function);
                    }
                }

                // read functions directory files
                var functionsDirectory = $"{_applicationHostOptions.CurrentValue.ScriptPath}/{ScriptConstants.FunctionMetadataFolderName}";
                var functionFiles = fileSystem
                    .Directory
                    .EnumerateFiles(functionsDirectory, "*.json")
                    .Select(file => file.Substring(functionsDirectory.Length + 1))
                    .Where(file => file != ScriptConstants.FunctionMetadataFileName)
                    .ToImmutableArray();
                foreach (var functionFile in functionFiles)
                {
                    var function = ReadFunctionMetadata(functionsDirectory, functionFile, fileSystem, workerConfigs);
                    if (function != null)
                    {
                        functions.Add(function);
                    }
                }

                return functions;
            }
        }

        private FunctionMetadata ReadFunctionMetadata(string functionDirectory, string functionFileName, IFileSystem fileSystem, IEnumerable<RpcWorkerConfig> workerConfigs)
        {
            using (_metricsLogger.LatencyEvent(string.Format(MetricEventNames.ReadFunctionMetadata, functionDirectory)))
            {
                string functionName = null;

                try
                {
                    // read the function config
                    if (!Utility.TryReadFunctionConfig(functionDirectory, functionFileName, out string json, fileSystem))
                    {
                        // not a function directory
                        return null;
                    }

                    if (functionFileName == ScriptConstants.FunctionMetadataFileName)
                    {
                        // use the name of the directory as the functionName
                        functionName = Path.GetFileName(functionDirectory);
                    }
                    else if (functionDirectory == ScriptConstants.FunctionMetadataFolderName)
                    {
                        // use the name of the file as the functionName
                        functionName = Path.GetFileName(functionFileName);
                    }

                    ValidateName(functionName);

                    JObject functionConfig = JObject.Parse(json);

                    return ParseFunctionMetadata(functionName, functionConfig, functionDirectory, fileSystem, workerConfigs);
                }
                catch (Exception ex)
                {
                    // log any unhandled exceptions and continue
                    Utility.AddFunctionError(_functionErrors, functionName, Utility.FlattenException(ex, includeSource: false), isFunctionShortName: true);
                }
                return null;
            }
        }

        internal void ValidateName(string name, bool isProxy = false)
        {
            if (!Utility.IsValidFunctionName(name))
            {
                throw new InvalidOperationException(string.Format("'{0}' is not a valid {1} name.", name, isProxy ? "proxy" : "function"));
            }
        }

        private FunctionMetadata ParseFunctionMetadata(string functionName, JObject configMetadata, string scriptDirectory, IFileSystem fileSystem, IEnumerable<RpcWorkerConfig> workerConfigs)
        {
            var functionMetadata = new FunctionMetadata
            {
                Name = functionName,
                FunctionDirectory = scriptDirectory
            };

            JArray bindingArray = (JArray)configMetadata["bindings"];
            if (bindingArray == null || bindingArray.Count == 0)
            {
                throw new FormatException("At least one binding must be declared.");
            }

            if (bindingArray != null)
            {
                foreach (JObject binding in bindingArray)
                {
                    BindingMetadata bindingMetadata = BindingMetadata.Create(binding);
                    functionMetadata.Bindings.Add(bindingMetadata);
                }
            }

            JToken isDirect;
            if (configMetadata.TryGetValue("configurationSource", StringComparison.OrdinalIgnoreCase, out isDirect))
            {
                var isDirectValue = isDirect.ToString();
                if (string.Equals(isDirectValue, "attributes", StringComparison.OrdinalIgnoreCase))
                {
                    functionMetadata.SetIsDirect(true);
                }
                else if (!string.Equals(isDirectValue, "config", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException($"Illegal value '{isDirectValue}' for 'configurationSource' property in {functionMetadata.Name}'.");
                }
            }
            functionMetadata.ScriptFile = DeterminePrimaryScriptFile((string)configMetadata["scriptFile"], scriptDirectory, fileSystem);
            if (!string.IsNullOrWhiteSpace(functionMetadata.ScriptFile))
            {
                functionMetadata.Language = ParseLanguage(functionMetadata.ScriptFile, workerConfigs);
            }
            functionMetadata.EntryPoint = (string)configMetadata["entryPoint"];
            return functionMetadata;
        }

        internal static string ParseLanguage(string scriptFilePath, IEnumerable<RpcWorkerConfig> workerConfigs)
        {
            // scriptFilePath is not required for HttpWorker
            if (string.IsNullOrEmpty(scriptFilePath))
            {
                return null;
            }

            // determine the script type based on the primary script file extension
            string extension = Path.GetExtension(scriptFilePath).ToLowerInvariant().TrimStart('.');
            var workerConfig = workerConfigs.FirstOrDefault(config => config.Description.Extensions.Contains("." + extension));
            if (workerConfig != null)
            {
                return workerConfig.Description.Language;
            }

            // If no worker claimed these extensions, use in-proc.
            switch (extension)
            {
                case "csx":
                case "cs":
                    return DotNetScriptTypes.CSharp;
                case "dll":
                    return DotNetScriptTypes.DotNetAssembly;
            }

            return null;
        }

        // Logic for this function is copied to:
        // https://github.com/projectkudu/kudu/blob/master/Kudu.Core/Functions/FunctionManager.cs
        // These two implementations must stay in sync!

        /// <summary>
        /// Determines which script should be considered the "primary" entry point script. Returns null if Primary script file cannot be determined
        /// </summary>
        internal static string DeterminePrimaryScriptFile(string scriptFile, string scriptDirectory, IFileSystem fileSystem = null)
        {
            fileSystem = fileSystem ?? FileUtility.Instance;

            // First see if there is an explicit primary file indicated
            // in config. If so use that.
            string functionPrimary = null;

            if (!string.IsNullOrEmpty(scriptFile))
            {
                string scriptPath = fileSystem.Path.Combine(scriptDirectory, scriptFile);
                if (!fileSystem.File.Exists(scriptPath))
                {
                    throw new FunctionConfigurationException("Invalid script file name configuration. The 'scriptFile' property is set to a file that does not exist.");
                }

                functionPrimary = scriptPath;
            }
            else
            {
                string[] functionFiles = fileSystem.Directory.EnumerateFiles(scriptDirectory)
                    .Where(p => fileSystem.Path.GetFileName(p).ToLowerInvariant() != ScriptConstants.FunctionMetadataFileName)
                    .ToArray();

                if (functionFiles.Length == 0)
                {
                    return null;
                }

                if (functionFiles.Length == 1)
                {
                    // if there is only a single file, that file is primary
                    functionPrimary = functionFiles[0];
                }
                else
                {
                    // if there is a "run" file, that file is primary,
                    // for Node, any index.js file is primary
                    functionPrimary = functionFiles.FirstOrDefault(p =>
                        fileSystem.Path.GetFileNameWithoutExtension(p).ToLowerInvariant() == "run" ||
                        fileSystem.Path.GetFileName(p).ToLowerInvariant() == "index.js");
                }
            }

            // Primary script file is not required for HttpWorker or any custom language worker
            if (string.IsNullOrEmpty(functionPrimary))
            {
                return null;
            }
            return Path.GetFullPath(functionPrimary);
        }
    }
}