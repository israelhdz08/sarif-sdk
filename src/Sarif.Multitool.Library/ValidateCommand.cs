﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Microsoft.CodeAnalysis.Sarif.Driver;
using Microsoft.CodeAnalysis.Sarif.Writers;
using Microsoft.Json.Schema;
using Microsoft.Json.Schema.Validation;

using Newtonsoft.Json;

namespace Microsoft.CodeAnalysis.Sarif.Multitool
{
#pragma warning disable CS0618
    public class ValidateCommand : MultithreadedAnalyzeCommandBase<SarifValidationContext, ValidateOptions>
#pragma warning restore CS0618
    {
        private List<Assembly> _defaultPlugInAssemblies;

        protected override IFileSystem FileSystem => throw new InvalidOperationException();

        public ValidateCommand(IFileSystem fileSystem = null) : base(fileSystem)
        {
        }

        public override IEnumerable<Assembly> DefaultPluginAssemblies
        {
            get
            {
                if (_defaultPlugInAssemblies == null)
                {
                    _defaultPlugInAssemblies = new List<Assembly>
                    {
                        this.GetType().Assembly
                    };
                }

                return _defaultPlugInAssemblies;
            }
        }

        protected override SarifValidationContext CreateContext(ValidateOptions options,
                                                                IAnalysisLogger logger,
                                                                RuntimeConditions runtimeErrors,
                                                                IFileSystem fileSystem,
                                                                PropertiesDictionary policy = null)
        {
            SarifValidationContext context = base.CreateContext(options, logger, runtimeErrors, fileSystem, policy);

            if (options != null)
            {
                context.SchemaFilePath = options.SchemaFilePath;
                context.UpdateInputsToCurrentSarif = options.UpdateInputsToCurrentSarif;
            }

            return context;
        }

        protected override void AnalyzeTarget(SarifValidationContext context,
                                              IEnumerable<Skimmer<SarifValidationContext>> skimmers,
                                              ISet<string> disabledSkimmers)
        {
            // The base class knows how to invoke the skimmers that implement smart validation,
            // but it doesn't know how to invoke schema validation, which has its own set of rules,
            // so we do that ourselves.
            //
            // Validate will return an empty file if there are any JSON syntax errors. 
            // In that case there's no point in going on.
            string sarifText =
                Validate(context.CurrentTarget.Uri.GetFilePath(),
                         context.SchemaFilePath,
                         context.Logger,
                         context.FileSystem,
                         context.UpdateInputsToCurrentSarif);

            if (!string.IsNullOrEmpty(sarifText))
            {
                context.InputLogContents = sarifText;
                context.InputLog = context.InputLogContents != null ? Deserialize(context.InputLogContents) : null;

                if (context.InputLog != null)
                {
                    // Everything's ready, so run all the skimmers.
                    base.AnalyzeTarget(context, skimmers, disabledSkimmers);
                }
            }
        }

        private static SarifLog Deserialize(string logContents)
        {
            SarifLog log = null;
            try
            {
                return JsonConvert.DeserializeObject<SarifLog>(logContents);
            }
            catch (JsonSerializationException)
            {
                // This exception can happen, for example, if a property required by the schema is
                // missing.
            }

            return log;
        }

        private string Validate(
            string instanceFilePath,
            string schemaFilePath,
            IAnalysisLogger logger,
            IFileSystem fileSystem,
            bool updateToCurrentSarifVersion = true)
        {
            string instanceText = null;

            try
            {
                instanceText = fileSystem.FileReadAllText(instanceFilePath);

                if (updateToCurrentSarifVersion)
                {
                    PrereleaseCompatibilityTransformer.UpdateToCurrentVersion(instanceText, formatting: Formatting.Indented, out instanceText);
                }

                PerformSchemaValidation(instanceText, instanceFilePath, schemaFilePath, logger);
            }
            catch (JsonSyntaxException ex)
            {
                Result result = ex.ToSarifResult();
                ReportResult(result, logger);

                // If the file isn't syntactically valid JSON, we won't be able to run
                // the skimmers, because they rely on being able to deserialized the file
                // into a SarifLog object.
                instanceText = null;
            }
            catch (SchemaValidationException ex)
            {
                ReportInvalidSchemaErrors(ex, schemaFilePath, logger);
            }
            // The framework will catch all other, unexpected exceptions, and it will
            // cause the tool to exit with a non-0 exit code.

            return instanceText;
        }

        private void PerformSchemaValidation(
            string instanceText,
            string instanceFilePath,
            string schemaFilePath,
            IAnalysisLogger logger)
        {
            string schemaText = null;

            if (schemaFilePath != null)
            {
                schemaText = FileSystem.FileReadAllText(schemaFilePath);
            }
            else
            {
                string schemaResource = "Microsoft.CodeAnalysis.Sarif.Multitool.sarif-2.1.0.json";

                using (Stream stream = this.GetType().Assembly.GetManifestResourceStream(schemaResource))
                using (var reader = new StreamReader(stream))
                {
                    schemaText = reader.ReadToEnd();
                }
            }

            JsonSchema schema = SchemaReader.ReadSchema(schemaText, schemaFilePath);

            var validator = new Validator(schema);
            Result[] results = validator.Validate(instanceText, instanceFilePath);

            ReportResults(results, logger);
        }

        private static void ReportInvalidSchemaErrors(
            SchemaValidationException ex,
            string schemaFile,
            IAnalysisLogger logger)
        {
            foreach (SchemaValidationException schemaValidationException in ex.WrappedExceptions)
            {
                Result result = ResultFactory.CreateResult(
                    schemaValidationException.JToken,
                    schemaValidationException.ErrorNumber,
                    schemaValidationException.Args);

                result.SetResultFile(schemaFile);
                ReportResult(result, logger);
            }
        }

        private static void ReportResults(
            Result[] results,
            IAnalysisLogger logger)
        {
            foreach (Result result in results)
            {
                ReportResult(result, logger);
            }
        }

        private static void ReportResult(
            Result result,
            IAnalysisLogger logger)
        {
            ReportingDescriptor rule = RuleFactory.GetRuleFromRuleId(result.RuleId);
            logger.Log(rule, result);
        }
    }
}
