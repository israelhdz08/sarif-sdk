﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.Sarif.Writers;

namespace Microsoft.CodeAnalysis.Sarif.Driver
{
    public abstract class MultithreadedAnalyzeCommandBase<TContext, TOptions> : PluginDriverCommand<TOptions>
        where TContext : IAnalysisContext, new()
        where TOptions : AnalyzeOptionsBase
    {
        public const string DefaultPolicyName = "default";

        // This is a unit test knob that configures the console output logger to capture its output
        // in a string builder. This is important to do because tooling behavior differs in non-trivial
        // ways depending on whether output is captured to a log file disk or not. In the latter case,
        // the captured output is useful to verify behavior.
        internal bool _captureConsoleOutput;
        internal ConsoleLogger _consoleLogger;

        private uint _fileContextsCount;
        private uint _ignoredFilesCount;
        private Channel<uint> _resultsWritingChannel;
        private Channel<uint> readyToScanChannel;
        private ConcurrentDictionary<uint, TContext> _fileContexts;

        public static bool RaiseUnhandledExceptionInDriverCode { get; set; }

        protected virtual Tool Tool { get; set; }

        public virtual FileFormat ConfigurationFormat => FileFormat.Json;

        protected MultithreadedAnalyzeCommandBase(IFileSystem fileSystem = null)
        {
            // TBD can we zap this?
            FileSystem = fileSystem ?? Sarif.FileSystem.Instance;
        }

        public string DefaultConfigurationPath
        {
            get
            {
                string currentDirectory = Path.GetDirectoryName(GetType().Assembly.Location);
                return Path.Combine(currentDirectory, "default.configuration.xml");
            }
        }

        public override int Run(TOptions options)
        {
            TContext context = default;
            return Run(options, ref context);
        }

        public virtual int Run(TOptions options, ref TContext globalContext)
        {
            try
            {
                globalContext ??= new TContext();
                if (options != null)
                {
                    globalContext = InitializeContextFromOptions(options, ref globalContext);
                }

                // We must make a copy of the global context reference
                // to utilize it in a separate thread.
                TContext methodLocalContext = globalContext;

                Task<int> analyzeTask = Task.Run(() =>
                {
                    return Run(methodLocalContext);
                }, globalContext.CancellationToken);

                int msDelay = globalContext.TimeoutInMilliseconds;

                if (Task.WhenAny(analyzeTask, Task.Delay(msDelay)).GetAwaiter().GetResult() == analyzeTask)
                {
                    return analyzeTask.Result;
                }

                Errors.LogAnalysisTimedOut(globalContext);
                return FAILURE;
            }
            catch (Exception ex)
            {
                globalContext.RuntimeExceptions ??= new List<Exception>();
                ProcessException(globalContext, ex);
            }
            finally
            {
                globalContext.Dispose();
            }

            return
                globalContext.RichReturnCode == true
                    ? (int)globalContext.RuntimeErrors
                    : FAILURE;
        }

        private void ProcessException(TContext globalContext, Exception ex)
        {
            if (ex is ExitApplicationException<ExitReason> eae)
            {
                globalContext.RuntimeExceptions.Add(ex);
                return;
            }

            if (ex is AggregateException ae)
            {
                foreach (Exception innerException in ae.InnerExceptions)
                {
                    ProcessException(globalContext, innerException);
                }
                return;
            }

            if (ex.InnerException != null)
            {
                ProcessException(globalContext, ex.InnerException);
                return;
            }

            if (ex is OperationCanceledException oce)
            {
                Errors.LogAnalysisCanceled(globalContext, oce);
                globalContext.RuntimeExceptions.Add(new ExitApplicationException<ExitReason>(SdkResources.ERR999_AnalysisCanceled, oce)
                {
                    ExitReason = ExitReason.AnalysisCanceled
                });
                return;
            }

            Errors.LogUnhandledEngineException(globalContext, ex);
            globalContext.RuntimeExceptions.Add(new ExitApplicationException<ExitReason>(DriverResources.MSG_UnexpectedApplicationExit, ex)
            {
                ExitReason = ExitReason.UnhandledExceptionInEngine
            });
        }

        private int Run(TContext globalContext)
        {
            bool succeeded = false;
            IDisposable disposableLogger = null;

            globalContext.FileSystem ??= FileSystem;
            globalContext = ValidateContext(globalContext);
            disposableLogger = globalContext.Logger as IDisposable;
            InitializeOutputFile(globalContext);

            // 1. Instantiate skimmers. We need to do this before initializing
            //    the output file so that we can preconstruct the tool 
            //    extensions data written to the SARIF file. Due to this ordering, 
            //    we won't emit any failures or notifications in this operation 
            //    to the log file itself: it will only appear in console output.
            ISet<Skimmer<TContext>> skimmers = CreateSkimmers(globalContext);

            // 2. Initialize skimmers. Initialize occurs a single time only. This
            //    step needs to occurs after initializing configuration in order
            //    to allow command-line override of rule settings
            skimmers = InitializeSkimmers(skimmers, globalContext);

            // 3. Log analysis initiation
            globalContext.Logger.AnalysisStarted();

            // 4. Run all multi-threaded analysis operations.
            AnalyzeTargets(globalContext, skimmers);

            // 5. For test purposes, raise an unhandled exception if indicated
            if (RaiseUnhandledExceptionInDriverCode)
            {
                throw new InvalidOperationException(GetType().Name);
            }

            // Analysis is complete. Generate our stopped event and also dispose
            // of any disposable logs (which will flush and release locks on
            // output files).
            globalContext.Logger.AnalysisStopped(globalContext.RuntimeErrors);
            disposableLogger?.Dispose();

            if ((globalContext.RuntimeErrors & ~RuntimeConditions.Nonfatal) == RuntimeConditions.None)
            {
                ProcessBaseline(globalContext);
                PostLogFile(globalContext);
            }

            succeeded = (globalContext.RuntimeErrors & ~RuntimeConditions.Nonfatal) == RuntimeConditions.None;

            return
                globalContext.RichReturnCode
                    ? (int)globalContext?.RuntimeErrors
                    : succeeded ? SUCCESS : FAILURE;
        }

        public virtual TContext InitializeContextFromOptions(TOptions options, ref TContext context)
        {
            context ??= new TContext();
            context.FileSystem ??= Sarif.FileSystem.Instance;

            // Our first action is to initialize an aggregating logger, which 
            // includes a console logger (for general reporting of conditions
            // that precede successfully creating an output log file).
            context.Logger ??= InitializeLogger(options);

            // Next, we initialize ourselves from disk-based configuration, 
            // if specified. This allows users to operate against configuration
            // XML but to override specific settings within it via options.
            context = InitializeConfiguration(options.ConfigurationFilePath, context);

            // TBD: observe that unless/until all options are nullable, that we
            // will always clobber loaded context data when processing options.
            // The 'Quiet' property shows the model, use of a nullable type.
            // We need to convert all remaining options to nullable. Note that
            // there's still a problem: we can't use the absence of a command-line
            // boolean argument as positive evidence that a context representation
            // should be overridden. i.e., say the context file specifies 'Quiet',
            // there's no way that the options can override this because a value
            // of false is implied by the absence of an explicit command-line arg.
            // One solution is remove all boolean args, as we did with --force, 
            // in preference of enum-driven settings.
            context.Quiet = options.Quiet != null ? options.Quiet.Value : context.Quiet;
            context.Recurse = options.Recurse;
            context.Threads = options.Threads > 0 ? options.Threads : Environment.ProcessorCount;
            context.PostUri = options.PostUri;
            context.AutomationId = options.AutomationId;
            context.OutputFilePath = options.OutputFilePath;
            context.AutomationGuid = options.AutomationGuid;
            context.BaselineFilePath = options.BaselineFilePath;
            context.Traces = InitializeStringSet(options.Trace);
            context.DataToInsert = options.DataToInsert.ToFlags();
            context.DataToRemove = options.DataToRemove.ToFlags();
            context.ResultKinds = options.ResultKinds;
            context.FailureLevels = options.FailureLevels;
            context.OutputFileOptions = options.OutputFileOptions.ToFlags();
            context.MaxFileSizeInKilobytes = options.MaxFileSizeInKilobytes != null ? options.MaxFileSizeInKilobytes.Value : context.MaxFileSizeInKilobytes;
            context.PluginFilePaths = options.PluginFilePaths?.ToImmutableHashSet();
            context.InsertProperties = InitializeStringSet(options.InsertProperties);
            context.TargetFileSpecifiers = InitializeStringSet(options.TargetFileSpecifiers);
            context.InvocationPropertiesToLog = InitializeStringSet(options.InvocationPropertiesToLog);

            context.TargetsProvider =
                OrderedFileSpecifier.Create(
                    context.TargetFileSpecifiers,
                    context.Recurse,
                    context.MaxFileSizeInKilobytes,
                    context.FileSystem);

            return context;
        }

        public virtual TContext ValidateContext(TContext globalContext)
        {
            bool succeeded = true;

            bool force = globalContext.OutputFileOptions.HasFlag(FilePersistenceOptions.ForceOverwrite);
            succeeded &= ValidateFile(globalContext,
                                      globalContext.OutputFilePath,
                                      shouldExist: force ? (bool?)null : false);

            succeeded &= ValidateBaselineFile(globalContext);

            bool required = !string.IsNullOrEmpty(globalContext.BaselineFilePath);
            succeeded &= ValidateFile(globalContext,
                                      globalContext.BaselineFilePath,
                                      shouldExist: required ? true : (bool?)null);

            required = globalContext.PluginFilePaths?.Any() == true;
            succeeded &= ValidateFiles(globalContext,
                                       globalContext.PluginFilePaths,
                                       shouldExist: required ? true : (bool?)null);

            if (!string.IsNullOrEmpty(globalContext.PostUri))
            {
                try
                {
                    var httpClient = new HttpClientWrapper();
                    HttpResponseMessage httpResponseMessage = httpClient.GetAsync(globalContext.PostUri).GetAwaiter().GetResult();
                    if (!httpResponseMessage.IsSuccessStatusCode)
                    {
                        globalContext.PostUri = null;
                        succeeded = false;
                    }
                }
                catch (Exception e)
                {
                    globalContext.PostUri = null;
                    globalContext.RuntimeErrors |= RuntimeConditions.ExceptionPostingLogFile;
                    globalContext.RuntimeExceptions ??= new List<Exception>();
                    globalContext.RuntimeExceptions.Add(e);
                }
                finally
                {
                    // TBD add logging if POST URI is null.
                }
            }

            succeeded &= ValidateInvocationPropertiesToLog(globalContext);

            if (!succeeded)
            {
                ThrowExitApplicationException(ExitReason.InvalidCommandLineOption);
            }

            return globalContext;
        }

        private static ISet<string> InitializeStringSet(IEnumerable<string> strings)
        {
            return strings?.Any() == true ?
                   new StringSet(strings) :
                   new StringSet();
        }

        private void MultithreadedAnalyzeTargets(TContext globalContext,
                                                 IEnumerable<Skimmer<TContext>> skimmers,
                                                 ISet<string> disabledSkimmers)
        {
            globalContext.CancellationToken.ThrowIfCancellationRequested();
            var channelOptions = new BoundedChannelOptions(25000)
            {
                SingleWriter = true,
                SingleReader = false,
            };
            readyToScanChannel = Channel.CreateBounded<uint>(channelOptions);

            channelOptions = new BoundedChannelOptions(25000)
            {
                SingleWriter = false,
                SingleReader = true,
            };
            _resultsWritingChannel = Channel.CreateBounded<uint>(channelOptions);

            var sw = Stopwatch.StartNew();
            Console.WriteLine($"THREADS: {globalContext.Threads}");

            // 1: First we initiate an asynchronous operation to locate disk files for
            // analysis, as specified in analysis configuration (file names, wildcards).
            Task<bool> enumerateFilesOnDisk = Task.Run(() => EnumerateFilesOnDiskAsync(globalContext));

            // 2: A dedicated set of threads pull scan targets and analyze them.
            //    On completing a scan, the thread writes the index of the 
            //    scanned item to a channel that drives logging.
            var workers = new Task[globalContext.Threads];
            for (int i = 0; i < globalContext.Threads; i++)
            {
                workers[i] = Task.Run(() => ScanTargetsAsync(globalContext, skimmers, disabledSkimmers));
            }

            // 3: A single-threaded consumer watches for completed scans
            //    and logs results, if any. This operation is single-threaded
            //    to ensure determinism in log output. i.e., any scan of the
            //    same targets using the same production code should produce
            //    a log file that is byte-for-byte identical to previous log.
            Task logScanResults = Task.Run(() => LogScanResultsAsync(globalContext));

            Task.WhenAll(workers)
                .ContinueWith(_ => _resultsWritingChannel.Writer.Complete())
                .Wait();

            enumerateFilesOnDisk.Wait();
            logScanResults.Wait();

            if (_ignoredFilesCount > 0)
            {
                Warnings.LogOneOrMoreFilesSkippedDueToSize(globalContext, _ignoredFilesCount);
            }

            Console.WriteLine();

            string id;
            if (globalContext.Traces.Contains(nameof(DefaultTraces.PeakWorkingSet)))
            {
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    id = $"TRC101.{nameof(DefaultTraces.PeakWorkingSet)}";
                    string memoryUsage = $"Peak working set: {currentProcess.PeakWorkingSet64 / 1024 / 1024}MB.";
                    LogToolNotification(globalContext.Logger, memoryUsage, id);
                }
            }

            if (globalContext.Traces.Contains(nameof(DefaultTraces.ScanTime)))
            {
                id = $"TRC101.{nameof(DefaultTraces.ScanTime)}";
                string timing = $"Done. {_fileContextsCount:n0} files scanned, elapsed time {sw.Elapsed}.";
                LogToolNotification(globalContext.Logger, timing, id);
            }
            else
            {
                Console.WriteLine($"Done. {_fileContextsCount:n0} files scanned.");
            }
        }

        private async Task LogScanResultsAsync(TContext globalContext)
        {
            uint currentIndex = 0;

            ChannelReader<uint> reader = _resultsWritingChannel.Reader;

            // Wait until there is work or the channel is closed.
            while (await reader.WaitToReadAsync())
            {
                // Loop while there is work to do.
                while (reader.TryRead(out uint item))
                {
                    // This condition can occur if currentIndex moves
                    // ahead in array processing due to operations
                    // against it by other threads. For this case,
                    // since the relevant file has already been
                    // processed, we just ignore this notification.
                    if (currentIndex > item) { break; }

                    TContext context;
                    context = _fileContexts[currentIndex];

                    while (context?.AnalysisComplete == true)
                    {
                        lock (globalContext)
                        {
                            globalContext.CurrentTarget = context.CurrentTarget;
                            LogCachingLogger(globalContext, (CachingLogger)context.Logger, clone: false);
                            globalContext.RuntimeErrors |= context.RuntimeErrors;
                            globalContext.CurrentTarget = null;
                        }
                        context.Dispose();

                        _fileContexts.TryRemove(currentIndex, out _);
                        _fileContexts.TryGetValue(++currentIndex, out context);
                    }
                }
            }
        }

        private static void LogCachingLogger(TContext globalContext, CachingLogger cachingLogger, bool clone = false)
        {
            // Today, the signal to generate hash data in log files is synonymous with a decision
            // to perform target file results-caching (where we only analyze a copy of a file, by
            // hash, a single time). We need to separate configuring this mechanism. Some scan 
            // scenarios, such as binary analysis + crawl of PDB, greatly benefit from this mechanism,
            // other scan scenarios, such as lightweight linting of large #'s of source files, 
            // experience significant memory pressure from it. Disabling caching altogether for now.
            // 
            // https://github.com/microsoft/sarif-sdk/issues/2620
            //
            // As a result of this gap, `clone` will always be false, because we have a per-file
            // (and not per-file-by-hash) logger instance.

            IDictionary<ReportingDescriptor, IList<Tuple<Result, int?>>> results = cachingLogger.Results;
            globalContext.CancellationToken.ThrowIfCancellationRequested();
            IEnumeratedArtifact artifact = globalContext.CurrentTarget;

            if (results?.Count > 0)
            {
                foreach (KeyValuePair<ReportingDescriptor, IList<Tuple<Result, int?>>> kv in results)
                {
                    globalContext.CancellationToken.ThrowIfCancellationRequested();
                    foreach (Tuple<Result, int?> tuple in kv.Value)
                    {
                        Result result = tuple.Item1;
                        Result currentResult = result;
                        if (clone)
                        {
                            Result clonedResult = result.DeepClone();

                            UpdateLocationsAndMessageWithCurrentUri(clonedResult.Locations, clonedResult.Message, artifact.Uri);

                            currentResult = clonedResult;
                        }

                        globalContext.Logger.Log(kv.Key, currentResult, tuple.Item2);
                    }
                }
            }

            if (cachingLogger.ToolNotifications != null)
            {
                foreach (Tuple<Notification, ReportingDescriptor> tuple in cachingLogger.ToolNotifications)
                {
                    globalContext.Logger.LogToolNotification(tuple.Item1, tuple.Item2);
                }
            }

            if (cachingLogger.ConfigurationNotifications != null)
            {
                foreach (Notification notification in cachingLogger.ConfigurationNotifications)
                {
                    Notification currentNotification = notification;
                    if (clone)
                    {
                        Notification clonedNotification = notification.DeepClone();
                        UpdateLocationsAndMessageWithCurrentUri(clonedNotification.Locations, notification.Message, artifact.Uri);

                        currentNotification = clonedNotification;
                    }

                    globalContext.Logger.LogConfigurationNotification(currentNotification);
                }

            }

            globalContext.Logger.TargetAnalyzed(globalContext);
        }

        private async Task<bool> EnumerateFilesOnDiskAsync(TContext context)
        {
            try
            {
                this._fileContextsCount = 0;
                this._fileContexts = new ConcurrentDictionary<uint, TContext>();

                await EnumerateFilesFromArtifactsProvider(context);
            }
            finally
            {
                readyToScanChannel.Writer.Complete();
            }

            if (context.TargetsProvider.Skipped?.Count > 0)
            {
                foreach (IEnumeratedArtifact artifact in context.TargetsProvider.Skipped)
                {
                    Notes.LogFileSkippedDueToSize(context, artifact.Uri.GetFilePath(), (long)artifact.SizeInBytes);
                }

                //TBD resolve type mismatch
                _ignoredFilesCount += (uint)context.TargetsProvider.Skipped.Count;
            }

            if (_fileContextsCount == 0)
            {
                Errors.LogNoValidAnalysisTargets(context);
                ThrowExitApplicationException(ExitReason.NoValidAnalysisTargets);
            }

            return true;
        }

        private async Task<bool> EnumerateFilesFromArtifactsProvider(TContext globalContext)
        {
            foreach (IEnumeratedArtifact artifact in globalContext.TargetsProvider.Artifacts)
            {
                globalContext.CancellationToken.ThrowIfCancellationRequested();

                TContext fileContext =
                    CreateContext(options: null,
                                  new CachingLogger(globalContext.FailureLevels, globalContext.ResultKinds),
                                  globalContext.RuntimeErrors,
                                  globalContext.FileSystem,
                                  globalContext.Policy);

                Debug.Assert(fileContext.Logger != null);
                fileContext.CurrentTarget = artifact;
                fileContext.CancellationToken = globalContext.CancellationToken;

                lock (globalContext)
                {
                    // We need to generate this event on the global logger, though as
                    // a result this event means 'target enumerated for analysis'
                    // rather than literally 'we are analyzing the target'.
                    //
                    // This call needs to be protected with a lock as the actual
                    // logging occurs on a separated thread.
                    globalContext.Logger.AnalyzingTarget(fileContext);
                }

                bool added = _fileContexts.TryAdd(_fileContextsCount, fileContext);
                Debug.Assert(added);

                await readyToScanChannel.Writer.WriteAsync(_fileContextsCount++);
            }

            // TBD get all skipped artifacts.

            return true;
        }

        private void EnqueueAllDirectories(TContext context, Queue<string> queue, string directory)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var sortedDiskItems = new SortedSet<string>();

            queue.Enqueue(directory);
            foreach (string childDirectory in FileSystem.DirectoryEnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                sortedDiskItems.Add(childDirectory);
            }

            foreach (string childDirectory in sortedDiskItems)
            {
                EnqueueAllDirectories(context, queue, childDirectory);
            }
        }

        private readonly ConcurrentDictionary<string, CachingLogger> _loggerCache = new ConcurrentDictionary<string, CachingLogger>();

        private async Task ScanTargetsAsync(TContext globalContext, IEnumerable<Skimmer<TContext>> skimmers, ISet<string> disabledSkimmers)
        {
            ChannelReader<uint> reader = readyToScanChannel.Reader;
            globalContext.CancellationToken.ThrowIfCancellationRequested();

            // Wait until there is work or the channel is closed.
            while (await reader.WaitToReadAsync())
            {
                // Loop while there is work to do.
                while (reader.TryRead(out uint item))
                {
                    TContext perFileContext = _fileContexts[item]; ;
                    perFileContext.CancellationToken.ThrowIfCancellationRequested();
                    DetermineApplicabilityAndAnalyze(perFileContext, skimmers, disabledSkimmers);
                    globalContext.RuntimeErrors |= perFileContext.RuntimeErrors;
                    if (perFileContext != null) { perFileContext.AnalysisComplete = true; }
                    await _resultsWritingChannel.Writer.WriteAsync(item);
                }
            }
        }

        protected virtual void ValidateOptions(TOptions options, TContext context)
        {
            bool succeeded = true;

            // TBD get rid of me.

            succeeded &= ValidateFile(context, options.OutputFilePath, shouldExist: null);
            succeeded &= ValidateFile(context, options.ConfigurationFilePath, shouldExist: true);
            succeeded &= ValidateFile(context, options.BaselineFilePath, shouldExist: true);
            succeeded &= ValidateFiles(context, options.PluginFilePaths, shouldExist: true);
            succeeded &= ValidateInvocationPropertiesToLog(context);
            succeeded &= options.ValidateOutputOptions(context);
            succeeded &= context.MaxFileSizeInKilobytes >= 0;

            succeeded &= ValidateOutputFileCanBeCreated(context);

            if (!succeeded)
            {
                ThrowExitApplicationException(ExitReason.InvalidCommandLineOption);
            }
        }

        internal AggregatingLogger InitializeLogger(AnalyzeOptionsBase analyzeOptions)
        {
            Tool ??= Tool.CreateFromAssemblyData();

            var logger = new AggregatingLogger();

            if (!(analyzeOptions.Quiet == true))
            {
                _consoleLogger = new ConsoleLogger(quietConsole: false, Tool.Driver.Name, analyzeOptions.FailureLevels, analyzeOptions.ResultKinds) { CaptureOutput = _captureConsoleOutput };
                logger.Loggers.Add(_consoleLogger);
            }

            return logger;
        }

        protected virtual TContext CreateContext(TOptions options,
                                                 IAnalysisLogger logger,
                                                 RuntimeConditions runtimeErrors,
                                                 IFileSystem fileSystem = null,
                                                 PropertiesDictionary policy = null)
        {
            var context = new TContext
            {
                Logger = logger,
                FileSystem = fileSystem ?? this.FileSystem,
                RuntimeErrors = runtimeErrors,
                Policy = policy ?? new PropertiesDictionary(),
            };

            return context;
        }

        /// <summary>
        /// Calculate the file to load the configuration from.
        /// </summary>
        /// <param name="options">Options</param>
        /// <returns>Configuration file path, or null if the built in configuration should be used.</returns>
        internal string GetConfigurationFileName(string configurationFilePath, IFileSystem fileSystem)
        {
            // If the user passes our default file name, return null, indicating that no
            // configuration should be loaded. As a result, we will pick up all runtime defaults.
            if (configurationFilePath == DefaultPolicyName)
            {
                return null;
            }

            if (string.IsNullOrEmpty(configurationFilePath))
            {
                // If a configuration file is not explicitly specified but we see that 
                // a default configuration file has been placed in the current working
                // directory, we will load it. TBD: ensure this location is alongside
                // the client executable?
                return fileSystem.FileExists(this.DefaultConfigurationPath)
                    ? this.DefaultConfigurationPath
                    : null;
            }

            return configurationFilePath;
        }

        protected virtual TContext InitializeConfiguration(string configurationFileName, TContext context)
        {
            context.Policy ??= new PropertiesDictionary();
            configurationFileName = GetConfigurationFileName(configurationFileName, context.FileSystem);
            context.ConfigurationFilePath = configurationFileName;

            if (!ValidateFile(context,
                              configurationFileName,
                              shouldExist: !string.IsNullOrEmpty(configurationFileName) ? true : (bool?)null))
            {
                // TBD exit reason update to invalid config?
                ThrowExitApplicationException(ExitReason.InvalidCommandLineOption);
            }

            if (string.IsNullOrEmpty(configurationFileName)) { return context; }

            string extension = Path.GetExtension(configurationFileName);

            var configuration = new PropertiesDictionary();
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                configuration.LoadFromXml(configurationFileName);
            }
            else if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                configuration.LoadFromJson(configurationFileName);
            }
            else if (ConfigurationFormat == FileFormat.Xml)
            {
                configuration.LoadFromXml(configurationFileName);
            }
            else
            {
                configuration.LoadFromJson(configurationFileName);
            }
            configuration.MergePreferFirst(context.Policy);
            context.Policy = configuration;
            return context;
        }

        public virtual void InitializeOutputFile(TContext globalContext)
        {
            string filePath = globalContext.OutputFilePath;

            if (!string.IsNullOrEmpty(filePath))
            {
                var aggregatingLogger = globalContext.Logger as AggregatingLogger;
                if (aggregatingLogger == null)
                {
                    aggregatingLogger = new AggregatingLogger();
                    aggregatingLogger.Loggers.Add(globalContext.Logger);
                    globalContext.Logger = aggregatingLogger;
                }

                InvokeCatchingRelevantIOExceptions
                (
                    () =>
                    {
                        FilePersistenceOptions logFilePersistenceOptions = globalContext.OutputFileOptions;

                        OptionallyEmittedData dataToInsert = globalContext.DataToInsert;
                        OptionallyEmittedData dataToRemove = globalContext.DataToRemove;

                        SarifLogger sarifLogger;

                        var run = new Run()
                        {
                            AutomationDetails = new RunAutomationDetails
                            {
                                Id = globalContext.AutomationId,
                                Guid = globalContext.AutomationGuid
                            },
                            Tool = Tool,
                        };

                        sarifLogger = new SarifLogger(globalContext.OutputFilePath,
                                                      logFilePersistenceOptions,
                                                      dataToInsert,
                                                      dataToRemove,
                                                      run,
                                                      analysisTargets: null,
                                                      quiet: globalContext.Quiet,
                                                      invocationTokensToRedact: GenerateSensitiveTokensList(),
                                                      invocationPropertiesToLog: globalContext.InvocationPropertiesToLog,
                                                      levels: globalContext.FailureLevels,
                                                      kinds: globalContext.ResultKinds,
                                                      insertProperties: globalContext.InsertProperties);

                        aggregatingLogger.Loggers.Add(sarifLogger);
                    },
                    (ex) =>
                    {
                        Errors.LogExceptionCreatingLogFile(globalContext, filePath, ex);
                        ThrowExitApplicationException(ExitReason.ExceptionCreatingLogFile, ex);
                    }
                );
            }
        }

        private static IEnumerable<string> GenerateSensitiveTokensList()
        {
            var result = new List<string>
            {
                Environment.MachineName,
                Environment.UserName,
                Environment.UserDomainName
            };

            string userDnsDomain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
            string logonServer = Environment.GetEnvironmentVariable("LOGONSERVER");

            if (!string.IsNullOrEmpty(userDnsDomain)) { result.Add(userDnsDomain); }
            if (!string.IsNullOrEmpty(logonServer)) { result.Add(logonServer); }

            return result;
        }

        public void InvokeCatchingRelevantIOExceptions(Action action, Action<Exception> exceptionHandler)
        {
            try
            {
                action();
            }
            catch (UnauthorizedAccessException ex)
            {
                exceptionHandler(ex);
            }
            catch (IOException ex)
            {
                exceptionHandler(ex);
            }
        }

        protected virtual ISet<Skimmer<TContext>> CreateSkimmers(TContext context)
        {
            IEnumerable<Skimmer<TContext>> skimmers;
            var result = new SortedSet<Skimmer<TContext>>(SkimmerIdComparer<TContext>.Instance);

            try
            {
                skimmers = CompositionUtilities.GetExports<Skimmer<TContext>>(RetrievePluginAssemblies(DefaultPluginAssemblies, context.PluginFilePaths));

                SupportedPlatform currentOS = GetCurrentRunningOS();
                foreach (Skimmer<TContext> skimmer in skimmers)
                {
                    if (skimmer.SupportedPlatforms.HasFlag(currentOS))
                    {
                        result.Add(skimmer);
                    }
                    else
                    {
                        Warnings.LogUnsupportedPlatformForRule(context, skimmer.Name, skimmer.SupportedPlatforms, currentOS);
                    }
                }
            }
            catch (Exception ex)
            {
                Errors.LogExceptionInstantiatingSkimmers(context, DefaultPluginAssemblies, ex);
                ThrowExitApplicationException(ExitReason.UnhandledExceptionInstantiatingSkimmers, ex);
            }

            if (result.Count == 0)
            {
                Errors.LogNoRulesLoaded(context);
                ThrowExitApplicationException(ExitReason.NoRulesLoaded);
            }
            return result;
        }

        private static SupportedPlatform GetCurrentRunningOS()
        {
            // RuntimeInformation is not present in NET452.
#if NET452
            return SupportedPlatform.Windows;
#else
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return SupportedPlatform.Linux;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return SupportedPlatform.OSX;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return SupportedPlatform.Windows;
            }
            else
            {
                return SupportedPlatform.Unknown;
            }
#endif
        }

        protected virtual void AnalyzeTargets(TContext context,
                                              IEnumerable<Skimmer<TContext>> skimmers)
        {
            if (skimmers == null)
            {
                Errors.LogNoRulesLoaded(context);
                ThrowExitApplicationException(ExitReason.NoRulesLoaded);
            }

            var disabledSkimmers = new SortedSet<string>();

            foreach (Skimmer<TContext> skimmer in skimmers)
            {
                PerLanguageOption<RuleEnabledState> ruleEnabledProperty =
                    DefaultDriverOptions.CreateRuleSpecificOption(skimmer, DefaultDriverOptions.RuleEnabled);

                RuleEnabledState ruleEnabled = context.Policy.GetProperty(ruleEnabledProperty);

                FailureLevel failureLevel = (ruleEnabled == RuleEnabledState.Default || ruleEnabled == RuleEnabledState.Disabled)
                    ? default
                    : (FailureLevel)Enum.Parse(typeof(FailureLevel), ruleEnabled.ToString());

                if (ruleEnabled == RuleEnabledState.Disabled)
                {
                    disabledSkimmers.Add(skimmer.Id);
                    Warnings.LogRuleExplicitlyDisabled(context, skimmer.Id);
                    context.RuntimeErrors |= RuntimeConditions.RuleWasExplicitlyDisabled;
                }
                else if (!skimmer.DefaultConfiguration.Enabled && ruleEnabled == RuleEnabledState.Default)
                {
                    // This skimmer is disabled by default, and the configuration file didn't mention it.
                    // So disable it, but don't complain that the rule was explicitly disabled.
                    disabledSkimmers.Add(skimmer.Id);
                }
                else if (skimmer.DefaultConfiguration.Level != failureLevel &&
                         ruleEnabled != RuleEnabledState.Default &&
                         ruleEnabled != RuleEnabledState.Disabled)
                {
                    skimmer.DefaultConfiguration.Level = failureLevel;
                }
            }

            if (disabledSkimmers.Count == skimmers.Count())
            {
                Errors.LogAllRulesExplicitlyDisabled(context);
                ThrowExitApplicationException(ExitReason.NoRulesLoaded);
            }

            this.CheckIncompatibleRules(skimmers, context, disabledSkimmers);

            MultithreadedAnalyzeTargets(context, skimmers, disabledSkimmers);
        }

        protected virtual TContext DetermineApplicabilityAndAnalyze(TContext context,
                                                                    IEnumerable<Skimmer<TContext>> skimmers,
                                                                    ISet<string> disabledSkimmers)
        {
            if (context.RuntimeExceptions != null)
            {
                Debug.Assert(context.RuntimeExceptions.Count == 1);
                Errors.LogExceptionLoadingTarget(context, context.RuntimeExceptions[0]);
                return context;
            }
            else if (!context.IsValidAnalysisTarget)
            {
                Warnings.LogExceptionInvalidTarget(context);
                return context;
            }

            var logger = (CachingLogger)context.Logger;
            logger.AnalyzingTarget(context);

            if (logger.CacheFinalized)
            {
                context.Logger = logger;
                logger.TargetAnalyzed(context);
                return context;
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            IEnumerable<Skimmer<TContext>> applicableSkimmers = DetermineApplicabilityForTarget(context, skimmers, disabledSkimmers);

            AnalyzeTarget(context, applicableSkimmers, disabledSkimmers);

            logger.TargetAnalyzed(context);
            return context;
        }

        internal static void UpdateLocationsAndMessageWithCurrentUri(IList<Location> locations, Message message, Uri updatedUri)
        {
            if (locations == null) { return; }

            foreach (Location location in locations)
            {
                ArtifactLocation artifactLocation = location.PhysicalLocation?.ArtifactLocation;
                if (artifactLocation == null) { continue; }

                if (message == null)
                {
                    artifactLocation.Uri = updatedUri;
                    continue;
                }

                string oldFilePath = artifactLocation.Uri.OriginalString;
                string newFilePath = updatedUri.OriginalString;

                string oldFileName = GetFileNameFromUri(artifactLocation.Uri);
                string newFileName = GetFileNameFromUri(updatedUri);

                if (!string.IsNullOrEmpty(oldFileName) && !string.IsNullOrEmpty(newFileName))
                {
                    for (int i = 0; i < message?.Arguments?.Count; i++)
                    {
                        if (message.Arguments[i] == oldFileName)
                        {
                            message.Arguments[i] = newFileName;
                        }

                        if (message.Arguments[i] == oldFilePath)
                        {
                            message.Arguments[i] = newFilePath;
                        }
                    }

                    if (message.Text != null)
                    {
                        message.Text = message.Text.Replace(oldFilePath, newFilePath);
                        message.Text = message.Text.Replace(oldFileName, newFileName);
                    }
                }

                artifactLocation.Uri = updatedUri;
            }
        }

        internal static string GetFileNameFromUri(Uri uri)
        {
            if (uri == null) { return null; }

            return Path.GetFileName(uri.OriginalString);
        }

        protected virtual void AnalyzeTarget(TContext context, IEnumerable<Skimmer<TContext>> skimmers, ISet<string> disabledSkimmers)
        {
            AnalyzeTargetHelper(context, skimmers, disabledSkimmers);
        }

        public static void AnalyzeTargetHelper(TContext context, IEnumerable<Skimmer<TContext>> skimmers, ISet<string> disabledSkimmers)
        {
            foreach (Skimmer<TContext> skimmer in skimmers)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (disabledSkimmers.Count > 0)
                {
                    lock (disabledSkimmers)
                    {
                        if (disabledSkimmers.Contains(skimmer.Id)) { continue; }
                    }
                }

                context.Rule = skimmer;

                try
                {
                    Stopwatch stopwatch = context.Traces.Contains(nameof(DefaultTraces.RuleScanTime))
                        ? Stopwatch.StartNew()
                        : null;

                    skimmer.Analyze(context);

                    Uri uri = context.CurrentTarget != null
                        ? context.CurrentTarget.Uri
                        : context.CurrentTarget.Uri;

                    if (stopwatch != null && uri.IsAbsoluteUri)
                    {
                        string file = uri.LocalPath;
                        string directory = Path.GetDirectoryName(file);
                        file = Path.GetFileName(file);

                        string id = $"TRC101.{nameof(DefaultTraces.RuleScanTime)}";
                        string timing = $"'{file}' : elapsed {stopwatch.Elapsed} : '{skimmer.Name}' : at '{directory}'";
                        LogToolNotification(context.Logger, timing, id, context.Rule);
                    }
                }
                catch (Exception ex)
                {
                    Errors.LogUnhandledRuleExceptionAnalyzingTarget(disabledSkimmers, context, ex);
                }
            }
        }

        protected virtual IEnumerable<Skimmer<TContext>> DetermineApplicabilityForTarget(
        TContext context,
        IEnumerable<Skimmer<TContext>> skimmers,
        ISet<string> disabledSkimmers)
        {
            return DetermineApplicabilityForTargetHelper(context, skimmers, disabledSkimmers);
        }

        public static IEnumerable<Skimmer<TContext>> DetermineApplicabilityForTargetHelper(
            TContext context,
            IEnumerable<Skimmer<TContext>> skimmers,
            ISet<string> disabledSkimmers)
        {
            var candidateSkimmers = new List<Skimmer<TContext>>();

            foreach (Skimmer<TContext> skimmer in skimmers)
            {
                if (disabledSkimmers.Count > 0)
                {
                    lock (disabledSkimmers)
                    {
                        if (disabledSkimmers.Contains(skimmer.Id)) { continue; }
                    }
                }

                string reasonForNotAnalyzing;
                context.Rule = skimmer;

                AnalysisApplicability applicability = AnalysisApplicability.Unknown;

                try
                {
                    applicability = skimmer.CanAnalyze(context, out reasonForNotAnalyzing);
                }
                catch (Exception ex)
                {
                    Errors.LogUnhandledRuleExceptionAssessingTargetApplicability(disabledSkimmers, context, ex);
                    continue;
                }

                switch (applicability)
                {
                    case AnalysisApplicability.NotApplicableToSpecifiedTarget:
                    {
                        Notes.LogNotApplicableToSpecifiedTarget(context, reasonForNotAnalyzing);
                        break;
                    }

                    case AnalysisApplicability.ApplicableToSpecifiedTarget:
                    {
                        candidateSkimmers.Add(skimmer);
                        break;
                    }
                }
            }
            return candidateSkimmers;
        }

        protected void ThrowExitApplicationException(ExitReason exitReason, Exception innerException = null)
        {
            throw new ExitApplicationException<ExitReason>(DriverResources.MSG_UnexpectedApplicationExit, innerException)
            {
                ExitReason = exitReason
            };
        }


        internal void CheckIncompatibleRules(IEnumerable<Skimmer<TContext>> skimmers, TContext context, ISet<string> disabledSkimmers)
        {
            var availableRules = new Dictionary<string, Skimmer<TContext>>();

            foreach (Skimmer<TContext> skimmer in skimmers)
            {
                if (disabledSkimmers.Contains(skimmer.Id))
                {
                    continue;
                }

                availableRules[skimmer.Id] = skimmer;
            }

            foreach (KeyValuePair<string, Skimmer<TContext>> entry in availableRules)
            {
                if (entry.Value.IncompatibleRuleIds?.Any() != true)
                {
                    continue;
                }

                foreach (string incompatibleRuleId in entry.Value.IncompatibleRuleIds)
                {
                    if (availableRules.ContainsKey(incompatibleRuleId))
                    {
                        Errors.LogIncompatibleRules(context, entry.Key, incompatibleRuleId);
                        ThrowExitApplicationException(ExitReason.IncompatibleRulesDetected);
                    }
                }
            }
        }

        protected virtual ISet<Skimmer<TContext>> InitializeSkimmers(ISet<Skimmer<TContext>> skimmers, TContext globalContext)
        {
            var disabledSkimmers = new SortedSet<Skimmer<TContext>>(SkimmerIdComparer<TContext>.Instance);

            // ONE-TIME initialization of skimmers. Do not call
            // Initialize more than once per skimmer instantiation
            foreach (Skimmer<TContext> skimmer in skimmers)
            {
                try
                {
                    globalContext.Rule = skimmer;
                    skimmer.Initialize(globalContext);
                }
                catch (Exception ex)
                {
                    globalContext.RuntimeErrors |= RuntimeConditions.ExceptionInSkimmerInitialize;
                    Errors.LogUnhandledExceptionInitializingRule(globalContext, ex);
                    disabledSkimmers.Add(skimmer);
                }
            }

            foreach (Skimmer<TContext> disabledSkimmer in disabledSkimmers)
            {
                skimmers.Remove(disabledSkimmer);
            }

            return skimmers;
        }

        protected static void LogToolNotification(IAnalysisLogger logger,
                                                  string message,
                                                  string id = null,
                                                  ReportingDescriptor associatedRule = null,
                                                  FailureLevel level = FailureLevel.Note,
                                                  Exception ex = null)
        {
            ExceptionData exceptionData = null;
            if (ex != null)
            {
                exceptionData = new ExceptionData
                {
                    Kind = ex.GetType().FullName,
                    Message = ex.Message,
                    Stack = Stack.CreateStacks(ex).FirstOrDefault()
                };
            }

            TextWriter writer = level == FailureLevel.Error ? Console.Error : Console.Out;
            writer.WriteLine(message);

            logger.LogToolNotification(new Notification
            {
                Level = level,
                Descriptor = new ReportingDescriptorReference
                {
                    Id = id
                },
                AssociatedRule = new ReportingDescriptorReference { Id = associatedRule?.Id },
                Message = new Message { Text = message },
                Exception = exceptionData,
                TimeUtc = DateTime.UtcNow
            },
                associatedRule);
        }
    }
}
