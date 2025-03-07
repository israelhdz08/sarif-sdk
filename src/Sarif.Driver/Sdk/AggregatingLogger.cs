﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Microsoft.CodeAnalysis.Sarif.Driver
{
    public class AggregatingLogger : IDisposable, IAnalysisLogger
    {
        public AggregatingLogger() : this(null)
        {
        }

        public AggregatingLogger(IEnumerable<IAnalysisLogger> loggers)
        {
            this.Loggers = loggers != null ?
                new List<IAnalysisLogger>(loggers) :
                new List<IAnalysisLogger>();
        }

        public IList<IAnalysisLogger> Loggers { get; set; }

        public void Dispose()
        {
            foreach (IAnalysisLogger logger in Loggers)
            {
                using (logger as IDisposable) { };
            }
        }

        public void AnalysisStarted()
        {
            foreach (IAnalysisLogger logger in Loggers)
            {
                logger.AnalysisStarted();
            }
        }

        public void AnalysisStopped(RuntimeConditions runtimeConditions)
        {
            foreach (IAnalysisLogger logger in Loggers)
            {
                logger.AnalysisStopped(runtimeConditions);
            }
        }

        public void AnalyzingTarget(IAnalysisContext context)
        {
            foreach (IAnalysisLogger logger in Loggers)
            {
                logger.AnalyzingTarget(context);
            }
        }

        public void TargetAnalyzed(IAnalysisContext context)
        {
            foreach (IAnalysisLogger logger in Loggers)
            {
                logger.TargetAnalyzed(context);
            }
        }

        public void Log(ReportingDescriptor rule, Result result, int? extensionIndex)
        {
            foreach (IAnalysisLogger logger in Loggers)
            {
                logger.Log(rule, result, extensionIndex);
            }
        }

        public void LogToolNotification(Notification notification, ReportingDescriptor associatedRule)
        {
            foreach (IAnalysisLogger logger in Loggers)
            {
                logger.LogToolNotification(notification, associatedRule);
            }
        }

        public void LogConfigurationNotification(Notification notification)
        {
            foreach (IAnalysisLogger logger in Loggers)
            {
                logger.LogConfigurationNotification(notification);
            }
        }
    }
}
