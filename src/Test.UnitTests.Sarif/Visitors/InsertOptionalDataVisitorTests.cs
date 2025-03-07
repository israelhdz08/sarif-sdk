﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;

using FluentAssertions;

using Microsoft.CodeAnalysis.Sarif.Driver;
using Microsoft.CodeAnalysis.Sarif.Writers;
using Microsoft.CodeAnalysis.Test.Utilities.Sarif;

using Moq;

using Newtonsoft.Json;

using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.Sarif.Visitors
{
    public class InsertOptionalDataVisitorTests : FileDiffingUnitTests, IClassFixture<DeletesOutputsDirectoryOnClassInitializationFixture>
    {
        private const string EnlistmentRoot = "REPLACED_AT_TEST_RUNTIME";
        private OptionallyEmittedData _currentOptionallyEmittedData;

        public InsertOptionalDataVisitorTests(ITestOutputHelper outputHelper, DeletesOutputsDirectoryOnClassInitializationFixture _) : base(outputHelper)
        {
        }

        protected override string ConstructTestOutputFromInputResource(string inputResourceName, object parameter)
        {
            SarifLog actualLog =
                PrereleaseCompatibilityTransformer.UpdateToCurrentVersion(GetInputSarifTextFromResource(inputResourceName),
                                                                          formatting: Formatting.Indented,
                                                                          updatedLog: out _);

            // Some of the tests operate on SARIF files that mention the absolute path of the file
            // that was "analyzed" (InsertOptionalDataVisitor.txt). That path depends on the repo
            // root, and so can vary depending on the machine where the tests are run. To avoid
            // this problem, both the input files and the expected output files contain a fixed
            // string "REPLACED_AT_TEST_RUNTIME" in place of the directory portion of the path. But some
            // of the tests must read the contents of the analyzed file (for instance, when the
            // test requires snippets or file hashes to be inserted). Those test require the actual
            // path. Therefore we replace the fixed string with the actual path, execute the
            // visitor, and then restore the fixed string so the actual output can be compared
            // to the expected output.
            string enlistmentRoot = GitHelper.Default.GetRepositoryRoot(Environment.CurrentDirectory, useCache: false);

            if (inputResourceName == "CoreTests-Relative.sarif")
            {
                Uri originalUri = actualLog.Runs[0].OriginalUriBaseIds["TESTROOT"].Uri;
                string uriString = originalUri.ToString();

                uriString = uriString.Replace(EnlistmentRoot, enlistmentRoot);

                actualLog.Runs[0].OriginalUriBaseIds["TESTROOT"] = new ArtifactLocation { Uri = new Uri(uriString, UriKind.Absolute) };

                var visitor = new InsertOptionalDataVisitor(_currentOptionallyEmittedData);
                visitor.Visit(actualLog.Runs[0]);

                // Restore the remanufactured URI so that file diffing succeeds.
                actualLog.Runs[0].OriginalUriBaseIds["TESTROOT"] = new ArtifactLocation { Uri = originalUri };

                // In some of the tests, the visitor added an originalUriBaseId for the repo root.
                // Adjust that one, too.
                string repoRootUriBaseId = InsertOptionalDataVisitor.GetUriBaseId(0);
                if (actualLog.Runs[0].OriginalUriBaseIds.TryGetValue(repoRootUriBaseId, out ArtifactLocation artifactLocation))
                {
                    Uri repoRootUri = artifactLocation.Uri;
                    string repoRootString = repoRootUri.ToString();
                    repoRootString = repoRootString.Replace(enlistmentRoot.Replace(@"\", "/"), EnlistmentRoot);

                    actualLog.Runs[0].OriginalUriBaseIds[repoRootUriBaseId] = new ArtifactLocation { Uri = new Uri(repoRootString, UriKind.Absolute) };
                }
            }
            else if (inputResourceName == "CoreTests-Absolute.sarif")
            {
                Uri originalUri = actualLog.Runs[0].Artifacts[0].Location.Uri;
                string uriString = originalUri.ToString();

                uriString = uriString.Replace(EnlistmentRoot, enlistmentRoot);

                actualLog.Runs[0].Artifacts[0].Location = new ArtifactLocation { Uri = new Uri(uriString, UriKind.Absolute) };

                var visitor = new InsertOptionalDataVisitor(_currentOptionallyEmittedData);
                visitor.Visit(actualLog.Runs[0]);

                // Restore the remanufactured URI so that file diffing matches
                actualLog.Runs[0].Artifacts[0].Location = new ArtifactLocation { Uri = originalUri };
            }
            else
            {
                var visitor = new InsertOptionalDataVisitor(_currentOptionallyEmittedData);
                visitor.Visit(actualLog.Runs[0]);
            }

            // Verify and remove Guids, because they'll vary with every run and can't be compared to a fixed expected output.
            if (_currentOptionallyEmittedData.HasFlag(OptionallyEmittedData.Guids))
            {
                for (int i = 0; i < actualLog.Runs[0].Results.Count; ++i)
                {
                    Result result = actualLog.Runs[0].Results[i];
                    result.Guid.Should().NotBeNull(because: "OptionallyEmittedData.Guids flag was set");

                    result.Guid = null;
                }
            }

            if (_currentOptionallyEmittedData.HasFlag(OptionallyEmittedData.VersionControlDetails))
            {
                VersionControlDetails versionControlDetails = actualLog.Runs[0].VersionControlProvenance[0];

                // Verify and replace the mapped directory (enlistment root), because it varies
                // from machine to machine.
                var mappedUri = new Uri(enlistmentRoot + @"\", UriKind.Absolute);
                versionControlDetails.MappedTo.Uri.Should().Be(mappedUri);
                versionControlDetails.MappedTo.Uri = new Uri($"file:///{EnlistmentRoot}/");

                // When OptionallyEmittedData includes any file-related content, the visitor inserts
                // an artifact that points to the enlistment root. So we have to verify and adjust
                // that as well.
                IList<Artifact> artifacts = actualLog.Runs[0].Artifacts;
                if (artifacts.Count >= 2)
                {
                    artifacts[1].Location.Uri.Should().Be(enlistmentRoot);
                    artifacts[1].Location.Uri = new Uri($"file:///{EnlistmentRoot}/");
                }

                // Verify and replace the remote repo URI, because it would be different in a fork.
                var gitHelper = new GitHelper();
                Uri remoteUri = gitHelper.GetRemoteUri(enlistmentRoot);

                versionControlDetails.RepositoryUri.Should().Be(remoteUri);
                versionControlDetails.RepositoryUri = new Uri("https://REMOTE_URI");

                // Verify and remove branch and revision id, because they vary from run to run.
                versionControlDetails.Branch.Should().NotBeNullOrEmpty(because: "OptionallyEmittedData.VersionControlInformation flag was set");
                versionControlDetails.Branch = null;

                versionControlDetails.RevisionId.Should().NotBeNullOrEmpty(because: "OptionallyEmittedData.VersionControlInformation flag was set");
                versionControlDetails.RevisionId = null;
            }

            return JsonConvert.SerializeObject(actualLog, Formatting.Indented);
        }

        private void RunTest(string inputResourceName, OptionallyEmittedData optionallyEmittedData)
        {
            _currentOptionallyEmittedData = optionallyEmittedData;
            string expectedOutputResourceName = Path.GetFileNameWithoutExtension(inputResourceName);
            expectedOutputResourceName = expectedOutputResourceName + "_" + optionallyEmittedData.ToString().Replace(", ", "+");
            RunTest(inputResourceName, expectedOutputResourceName);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsHashes()
        {
            RunTest("CoreTests-Relative.sarif", OptionallyEmittedData.Hashes);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsTextFilesWithRelativeUris()
        {
            RunTest("CoreTests-Relative.sarif", OptionallyEmittedData.TextFiles);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsTextFilesWithAbsoluteUris()
        {
            RunTest("CoreTests-Absolute.sarif", OptionallyEmittedData.TextFiles);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsRegionSnippets()
        {
            RunTest("CoreTests-Relative.sarif", OptionallyEmittedData.RegionSnippets);
        }

        [Fact]
        public void InsertOptionalDataVisitor_PersistsFlattenedMessages()
        {
            RunTest("CoreTests-Relative.sarif", OptionallyEmittedData.FlattenedMessages);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsContextRegionSnippets()
        {
            RunTest("CoreTests-Relative.sarif", OptionallyEmittedData.ContextRegionSnippets);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsComprehensiveRegionProperties()
        {
            RunTest("CoreTests-Relative.sarif", OptionallyEmittedData.ComprehensiveRegionProperties);
        }

        [Fact]
        public void InsertOptionalDataVisitor_PersistsGuids()
        {
            // NOTE: Test adding Guids, but validation is in test code, not diff, as Guids vary with each run.
            RunTest("CoreTests-Relative.sarif", OptionallyEmittedData.Guids);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsVersionControlInformation()
        {
            RunTest("CoreTests-Relative.sarif", OptionallyEmittedData.VersionControlDetails);
        }

        [Fact]
        public void InsertOptionalDataVisitor_PersistsNone()
        {
            RunTest("CoreTests-Relative.sarif");
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsHashesAndTextFiles()
        {
            RunTest("CoreTests-Relative.sarif",
                OptionallyEmittedData.TextFiles |
                OptionallyEmittedData.Hashes);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsContextRegionSnippetPartialFingerprints()
        {
            RunTest("CoreTests-Relative.sarif", OptionallyEmittedData.ContextRegionSnippetPartialFingerprints);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_PersistsAll()
        {
            RunTest("CoreTests-Relative.sarif",
                OptionallyEmittedData.All);
        }

        [Fact]
        public void InsertOptionalDataVisitor_ContextRegionSnippets_DoesNotFail_TopLevelOriginalUriBaseIdUriMissing()
        {
            RunTest("TopLevelOriginalUriBaseIdUriMissing.sarif",
                OptionallyEmittedData.ContextRegionSnippets);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_SkipsExistingRepoRootSymbolsAndHandlesMultipleRoots()
        {
            const string ParentRepoRoot = @"C:\repo";
            const string ParentRepoBranch = "users/mary/my-feature";
            const string ParentRepoCommit = "11111";

            const string SubmoduleRepoRoot = @"C:\repo\submodule";
            const string SubmoduleBranch = "main";
            const string SubmoduleCommit = "22222";

            var mockFileSystem = new Mock<IFileSystem>();

            mockFileSystem.Setup(x => x.DirectoryExists(It.IsAny<string>())).Returns(false);
            mockFileSystem.Setup(x => x.FileExists(It.IsAny<string>())).Returns(false);

            mockFileSystem.Setup(x => x.DirectoryExists(@$"{ParentRepoRoot}\.git")).Returns(true);
            mockFileSystem.Setup(x => x.DirectoryExists(@$"{ParentRepoRoot}")).Returns(true);
            mockFileSystem.Setup(x => x.DirectoryExists(@$"{ParentRepoRoot}\src")).Returns(true);
            mockFileSystem.Setup(x => x.FileExists(@$"{ParentRepoRoot}\src\File.cs")).Returns(true);

            mockFileSystem.Setup(x => x.DirectoryExists($@"{SubmoduleRepoRoot}\.git")).Returns(false);
            mockFileSystem.Setup(x => x.DirectoryExists(@$"{ParentRepoRoot}\submodule")).Returns(true);
            mockFileSystem.Setup(x => x.DirectoryExists(@$"{SubmoduleRepoRoot}\src")).Returns(true);
            mockFileSystem.Setup(x => x.FileExists(@$"{SubmoduleRepoRoot}\src\File2.cs")).Returns(true);

            mockFileSystem.Setup(x => x.FileExists(GitHelper.s_expectedGitExePath)).Returns(true);

            var mockProcessRunner = new Mock<GitHelper.ProcessRunner>();

            mockProcessRunner.Setup(x => x(@$"{ParentRepoRoot}", GitHelper.s_expectedGitExePath, "rev-parse --show-toplevel")).Returns(ParentRepoRoot);
            mockProcessRunner.Setup(x => x(@$"{ParentRepoRoot}\src", GitHelper.s_expectedGitExePath, "rev-parse --show-toplevel")).Returns(ParentRepoRoot);
            mockProcessRunner.Setup(x => x($@"{ParentRepoRoot}\", GitHelper.s_expectedGitExePath, "remote get-url origin")).Returns(ParentRepoRoot);
            mockProcessRunner.Setup(x => x($@"{ParentRepoRoot}\", GitHelper.s_expectedGitExePath, "rev-parse --abbrev-ref HEAD")).Returns(ParentRepoBranch);
            mockProcessRunner.Setup(x => x($@"{ParentRepoRoot}\", GitHelper.s_expectedGitExePath, "rev-parse --verify HEAD")).Returns(ParentRepoCommit);

            mockProcessRunner.Setup(x => x(@$"{SubmoduleRepoRoot}", GitHelper.s_expectedGitExePath, "rev-parse --show-toplevel")).Returns(SubmoduleRepoRoot);
            mockProcessRunner.Setup(x => x(@$"{SubmoduleRepoRoot}\src", GitHelper.s_expectedGitExePath, "rev-parse --show-toplevel")).Returns(SubmoduleRepoRoot);
            mockProcessRunner.Setup(x => x(@$"{SubmoduleRepoRoot}\", GitHelper.s_expectedGitExePath, "remote get-url origin")).Returns(SubmoduleRepoRoot);
            mockProcessRunner.Setup(x => x(@$"{SubmoduleRepoRoot}\", GitHelper.s_expectedGitExePath, "rev-parse --abbrev-ref HEAD")).Returns(SubmoduleBranch);
            mockProcessRunner.Setup(x => x(@$"{SubmoduleRepoRoot}\", GitHelper.s_expectedGitExePath, "rev-parse --verify HEAD")).Returns(SubmoduleCommit);

            var run = new Run
            {
                OriginalUriBaseIds = new Dictionary<string, ArtifactLocation>
                {
                    // Called "REPO_ROOT" but doesn't actually point to a repo.
                    ["REPO_ROOT"] = new ArtifactLocation
                    {
                        Uri = new Uri(@"C:\dir1\dir2\", UriKind.Absolute)
                    }
                },
                Results = new List<Result>
                {
                    new Result
                    {
                        Locations = new List<Location>
                        {
                            new Location
                            {
                                PhysicalLocation = new PhysicalLocation
                                {
                                    ArtifactLocation = new ArtifactLocation
                                    {
                                        // The visitor will encounter this file and notice that it
                                        // is under a repo root. It will invent a uriBaseId symbol
                                        // for this repo. Since REPO_ROOT is already taken, it will
                                        // choose REPO_ROOT_2
                                        Uri = new Uri(@$"{ParentRepoRoot}\src\File.cs", UriKind.Absolute)
                                    }
                                }
                            },
                            new Location
                            {
                                PhysicalLocation = new PhysicalLocation
                                {
                                    ArtifactLocation = new ArtifactLocation
                                    {
                                        Uri = new Uri(@$"{SubmoduleRepoRoot}\src\File2.cs", UriKind.Absolute)
                                    }
                                }
                            }
                        }
                    }
                }
            };

            var properties = new List<string>
            {
                "runs[].invocations[].versionControlProvenance.properties.organizationId=orgId",
                "runs[].invocations[].versionControlProvenance.properties.projectId=projId"
            };

            var visitor = new InsertOptionalDataVisitor(
                OptionallyEmittedData.VersionControlDetails,
                originalUriBaseIds: null,
                fileSystem: mockFileSystem.Object,
                processRunner: mockProcessRunner.Object,
                insertProperties: properties);
            visitor.Visit(run);

            run.VersionControlProvenance[0].MappedTo.Uri.LocalPath.Should().Be($@"{ParentRepoRoot}\");
            run.VersionControlProvenance[0].Branch.Should().Be(ParentRepoBranch);
            run.VersionControlProvenance[0].RevisionId.Should().Be(ParentRepoCommit);
            run.VersionControlProvenance[0].GetProperty("organizationId").Should().Be("orgId");
            run.VersionControlProvenance[0].GetProperty("projectId").Should().Be("projId");

            run.VersionControlProvenance[1].MappedTo.Uri.LocalPath.Should().Be($@"{SubmoduleRepoRoot}\");
            run.VersionControlProvenance[1].Branch.Should().Be(SubmoduleBranch);
            run.VersionControlProvenance[1].RevisionId.Should().Be(SubmoduleCommit);
            run.VersionControlProvenance[1].GetProperty("organizationId").Should().Be("orgId");
            run.VersionControlProvenance[1].GetProperty("projectId").Should().Be("projId");

            run.OriginalUriBaseIds["REPO_ROOT_2"].Uri.LocalPath.Should().Be($@"{ParentRepoRoot}\");

            IList<Location> resultLocations = run.Results[0].Locations;

            ArtifactLocation resultArtifactLocation = resultLocations[0].PhysicalLocation.ArtifactLocation;
            resultArtifactLocation.Uri.OriginalString.Should().Be("src/File.cs");
            resultArtifactLocation.UriBaseId.Should().Be("REPO_ROOT_2");

            resultArtifactLocation = resultLocations[1].PhysicalLocation.ArtifactLocation;
            resultArtifactLocation.Uri.OriginalString.Should().Be("src/File2.cs");
            resultArtifactLocation.UriBaseId.Should().Be("REPO_ROOT_3");
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_CanVisitIndividualResultsInASuppliedRun()
        {
            const string TestFileContents =
@"One
Two
Three";
            const string ExpectedSnippet = "Two";

            using (var tempFile = new TempFile(".txt"))
            {
                string tempFilePath = tempFile.Name;
                string tempFileName = Path.GetFileName(tempFilePath);
                string tempFileDirectory = Path.GetDirectoryName(tempFilePath);

                File.WriteAllText(tempFilePath, TestFileContents);

                var run = new Run
                {
                    OriginalUriBaseIds = new Dictionary<string, ArtifactLocation>
                    {
                        [TestData.TestRootBaseId] = new ArtifactLocation
                        {
                            Uri = new Uri(tempFileDirectory, UriKind.Absolute)
                        }
                    },
                    Results = new List<Result>
                    {
                        new Result
                        {
                            Locations = new List<Location>
                            {
                                new Location
                                {
                                    PhysicalLocation = new PhysicalLocation
                                    {
                                        ArtifactLocation = new ArtifactLocation
                                        {
                                            Uri = new Uri(tempFileName, UriKind.Relative),
                                            UriBaseId = TestData.TestRootBaseId
                                        },
                                        Region = new Region
                                        {
                                            StartLine = 2
                                        }
                                    }
                                }
                            }
                        }
                    }
                };

                var visitor = new InsertOptionalDataVisitor(OptionallyEmittedData.RegionSnippets, run, insertProperties: null);

                visitor.VisitResult(run.Results[0]);

                run.Results[0].Locations[0].PhysicalLocation.Region.Snippet.Text.Should().Be(ExpectedSnippet);
            }
        }

        [Fact]
        public void InsertOptionalDataVisitor_ContextRegionSnippetPartialFingerprintsFromExistingContextRegionSnippet()
        {
            string fileName = TempFile.CreateTempName();
            string fileDirectory = Path.GetDirectoryName(fileName);

            const string ContextRegionSnippet =
@"One
Two
Three";
            string expectedPartialFingerprintHash = HashUtilities.ComputeStringSha256Hash(ContextRegionSnippet);

            var run = new Run
            {
                OriginalUriBaseIds =
                    new Dictionary<string, ArtifactLocation>
                    {
                        [TestData.TestRootBaseId] = new ArtifactLocation
                        {
                            Uri = new Uri(fileDirectory, UriKind.Absolute)
                        }
                    },
                Results = new List<Result>
                {
                    new Result
                    {
                        Locations = new List<Location>
                        {
                            new Location
                            {
                                PhysicalLocation = new PhysicalLocation
                                {
                                    ArtifactLocation = new ArtifactLocation
                                    {
                                        Uri = new Uri(fileName, UriKind.Relative),
                                        UriBaseId = TestData.TestRootBaseId
                                    },
                                    ContextRegion = new Region
                                    {
                                        Snippet = new ArtifactContent
                                        {
                                            Text = ContextRegionSnippet
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            var visitor = new InsertOptionalDataVisitor(OptionallyEmittedData.ContextRegionSnippetPartialFingerprints,
                run, insertProperties: null);

            visitor.VisitResult(run.Results[0]);

            run.Results[0].PartialFingerprints[InsertOptionalDataVisitor.ContextRegionHash].Should()
                .Be(expectedPartialFingerprintHash);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void InsertOptionalDataVisitor_ContextRegionSnippetPartialFingerprintsAlreadyExist(bool overwriteExistingData)
        {
            string fileName = TempFile.CreateTempName();
            string fileDirectory = Path.GetDirectoryName(fileName);

            const string ContextRegionSnippet =
@"One
Two
Three";
            const string ExistingPartialFingerprintHash = "123";

            string expectedPartialFingerprintHash = overwriteExistingData
                ? HashUtilities.ComputeStringSha256Hash(ContextRegionSnippet)
                : ExistingPartialFingerprintHash;

            OptionallyEmittedData dataToInsert = overwriteExistingData
                ? OptionallyEmittedData.ContextRegionSnippetPartialFingerprints | OptionallyEmittedData.OverwriteExistingData
                : OptionallyEmittedData.ContextRegionSnippetPartialFingerprints;

            var run = new Run
            {
                OriginalUriBaseIds =
                    new Dictionary<string, ArtifactLocation>
                    {
                        [TestData.TestRootBaseId] = new ArtifactLocation
                        {
                            Uri = new Uri(fileDirectory, UriKind.Absolute)
                        }
                    },
                Results = new List<Result>
                {
                    new Result
                    {
                        Locations = new List<Location>
                        {
                            new Location
                            {
                                PhysicalLocation = new PhysicalLocation
                                {
                                    ArtifactLocation = new ArtifactLocation
                                    {
                                        Uri = new Uri(fileName, UriKind.Relative),
                                        UriBaseId = TestData.TestRootBaseId
                                    },
                                    ContextRegion = new Region
                                    {
                                        Snippet = new ArtifactContent
                                        {
                                            Text = ContextRegionSnippet
                                        }
                                    }
                                }
                            }
                        },
                        PartialFingerprints = new Dictionary<string, string>
                        {
                            { InsertOptionalDataVisitor.ContextRegionHash, ExistingPartialFingerprintHash }
                        }
                    }
                }
            };

            var visitor = new InsertOptionalDataVisitor(dataToInsert, run, insertProperties: null);

            visitor.VisitResult(run.Results[0]);

            run.Results[0].PartialFingerprints[InsertOptionalDataVisitor.ContextRegionHash].Should()
                .Be(expectedPartialFingerprintHash);
        }

        private const int RuleIndex = 0;
        private const string RuleId = nameof(RuleId);
        private const string NotificationId = nameof(NotificationId);

        private const string SharedMessageId = nameof(SharedMessageId);
        private const string SharedKeyRuleMessageValue = nameof(SharedKeyRuleMessageValue);
        private const string SharedKeyGlobalMessageValue = nameof(SharedKeyGlobalMessageValue);

        private const string UniqueRuleMessageId = nameof(UniqueRuleMessageId);
        private const string UniqueRuleMessageValue = nameof(UniqueRuleMessageValue);

        private const string UniqueGlobalMessageId = nameof(UniqueGlobalMessageId);
        private const string UniqueGlobalMessageValue = nameof(UniqueGlobalMessageValue);

        private static Run CreateBasicRunForMessageStringLookupTesting()
        {
            // Returns a run object that defines unique string instances both
            // for an individual rule and in the global strings object. Also
            // defines values for a key that is shared between the rule object
            // and the global table. Used for evaluating string look-up semantics.
            var run = new Run
            {
                Results = new List<Result> { }, // add non-null collections for convenience
                Invocations = new List<Invocation>
                {
                    new Invocation
                    {
                        ToolExecutionNotifications = new List<Notification>{ },
                        ToolConfigurationNotifications = new List<Notification>{ }
                    }
                },
                Tool = new Tool
                {
                    Driver = new ToolComponent
                    {
                        GlobalMessageStrings = new Dictionary<string, MultiformatMessageString>
                        {
                            [UniqueGlobalMessageId] = new MultiformatMessageString { Text = UniqueGlobalMessageValue },
                            [SharedMessageId] = new MultiformatMessageString { Text = SharedKeyGlobalMessageValue }
                        },
                        Rules = new List<ReportingDescriptor>
                        {
                            new ReportingDescriptor
                            {
                                Id = RuleId,
                                MessageStrings = new Dictionary<string, MultiformatMessageString>
                                {
                                    [UniqueRuleMessageId] = new MultiformatMessageString { Text = UniqueRuleMessageValue },
                                    [SharedMessageId] = new MultiformatMessageString { Text = SharedKeyRuleMessageValue }
                                }
                            }
                        }
                    }
                }
            };

            return run;
        }

        [Fact]
        public void InsertOptionalDataVisitor_FlattensMessageStringsInResult()
        {
            Run run = CreateBasicRunForMessageStringLookupTesting();

            run.Results.Add(
                new Result
                {
                    RuleId = RuleId,
                    RuleIndex = RuleIndex,
                    Message = new Message
                    {
                        Id = UniqueGlobalMessageId
                    }
                });

            run.Results.Add(
                new Result
                {
                    RuleId = RuleId,
                    RuleIndex = RuleIndex,
                    Message = new Message
                    {
                        Id = UniqueRuleMessageId
                    }
                });

            run.Results.Add(
                new Result
                {
                    RuleId = RuleId,
                    RuleIndex = RuleIndex,
                    Message = new Message
                    {
                        Id = SharedMessageId
                    }
                });

            var visitor = new InsertOptionalDataVisitor(OptionallyEmittedData.FlattenedMessages);
            visitor.Visit(run);

            run.Results[0].Message.Text.Should().Be(UniqueGlobalMessageValue);
            run.Results[1].Message.Text.Should().Be(UniqueRuleMessageValue);

            // Prefer rule-specific value in the event of a message id collision
            run.Results[2].Message.Text.Should().Be(SharedKeyRuleMessageValue);
        }

        [Fact]
        public void InsertOptionalDataVisitor_FlattensMessageStringsInNotification()
        {
            Run run = CreateBasicRunForMessageStringLookupTesting();

            IList<Notification> toolNotifications = run.Invocations[0].ToolExecutionNotifications;
            IList<Notification> configurationNotifications = run.Invocations[0].ToolConfigurationNotifications;

            // Shared message id with no overriding rule id
            toolNotifications.Add(
                new Notification
                {
                    Descriptor = new ReportingDescriptorReference
                    {
                        Id = NotificationId
                    },
                    Message = new Message { Id = SharedMessageId }
                });
            configurationNotifications.Add(toolNotifications[0]);

            // Notification that refers to a rule that does not contain a message with
            // the same id as the specified notification id.In this case it is no surprise
            // that the message comes from the global string table.
            toolNotifications.Add(
                new Notification
                {
                    Descriptor = new ReportingDescriptorReference
                    {
                        Id = NotificationId
                    },
                    AssociatedRule = new ReportingDescriptorReference
                    {
                        Index = RuleIndex
                    },
                    Message = new Message { Id = UniqueGlobalMessageId }
                });
            configurationNotifications.Add(toolNotifications[1]);

            // Notification that refers to a rule that contains a message with the same
            // id as the specified notification message id. The message should still be
            // retrieved from the global strings table.
            toolNotifications.Add(
                new Notification
                {
                    Descriptor = new ReportingDescriptorReference
                    {
                        Id = NotificationId
                    },
                    AssociatedRule = new ReportingDescriptorReference
                    {
                        Index = RuleIndex
                    },
                    Message = new Message { Id = SharedMessageId }
                });
            configurationNotifications.Add(toolNotifications[2]);

            var visitor = new InsertOptionalDataVisitor(OptionallyEmittedData.FlattenedMessages);
            visitor.Visit(run);

            toolNotifications[0].Message.Text.Should().Be(SharedKeyGlobalMessageValue);
            configurationNotifications[0].Message.Text.Should().Be(SharedKeyGlobalMessageValue);

            toolNotifications[1].Message.Text.Should().Be(UniqueGlobalMessageValue);
            configurationNotifications[1].Message.Text.Should().Be(UniqueGlobalMessageValue);

            toolNotifications[2].Message.Text.Should().Be(SharedKeyGlobalMessageValue);
            configurationNotifications[2].Message.Text.Should().Be(SharedKeyGlobalMessageValue);
        }

        [Fact]
        public void InsertOptionalDataVisitor_FlattensMessageStringsInFix()
        {
            Run run = CreateBasicRunForMessageStringLookupTesting();

            run.Results.Add(
                new Result
                {
                    RuleId = RuleId,
                    RuleIndex = RuleIndex,
                    Message = new Message
                    {
                        Text = "Some testing occurred."
                    },
                    Fixes = new List<Fix>
                    {
                        new Fix
                        {
                            Description = new Message
                            {
                                Id = UniqueGlobalMessageId
                            }
                        },
                        new Fix
                        {
                            Description = new Message
                            {
                                Id = UniqueRuleMessageId
                            }
                        },
                        new Fix
                        {
                            Description = new Message
                            {
                                Id = SharedMessageId
                            }
                        }
                    }
                });
            run.Results.Add(
                new Result
                {
                    RuleId = "RuleWithNoRuleDescriptor",
                    Message = new Message
                    {
                        Text = "Some testing occurred."
                    },
                    Fixes = new List<Fix>
                    {
                        new Fix
                        {
                            Description = new Message
                            {
                                Id = SharedMessageId
                            }
                        }
                    }
                });

            var visitor = new InsertOptionalDataVisitor(OptionallyEmittedData.FlattenedMessages);
            visitor.Visit(run);

            run.Results[0].Fixes[0].Description.Text.Should().Be(UniqueGlobalMessageValue);
            run.Results[0].Fixes[1].Description.Text.Should().Be(UniqueRuleMessageValue);

            // Prefer rule-specific value in the event of a message id collision
            run.Results[0].Fixes[2].Description.Text.Should().Be(SharedKeyRuleMessageValue);

            // Prefer global value in the event of no rules metadata
            run.Results[1].Fixes[0].Description.Text.Should().Be(SharedKeyGlobalMessageValue);
        }

        [Fact]
        [Trait(TestTraits.WindowsOnly, "true")]
        public void InsertOptionalDataVisitor_ResolvesOriginalUriBaseIds()
        {
            string inputFileName = "InsertOptionalDataVisitor.txt";
            string inputFileText = GetResourceText(inputFileName);
            string testDirectory = Path.GetDirectoryName(GetResourceDiskPath(inputFileName)) + Path.DirectorySeparatorChar;
            string uriBaseId = "TEST_DIR";

            IDictionary<string, ArtifactLocation> originalUriBaseIds = new Dictionary<string, ArtifactLocation> { { uriBaseId, new ArtifactLocation { Uri = new Uri(testDirectory, UriKind.Absolute) } } };

            var run = new Run()
            {
                DefaultEncoding = "UTF-8",
                OriginalUriBaseIds = null,
                Results = new[]
                {
                    new Result()
                    {
                        Locations = new []
                        {
                            new Location
                            {
                                PhysicalLocation = new PhysicalLocation
                                {
                                     ArtifactLocation = new ArtifactLocation
                                     {
                                        Uri = new Uri(inputFileName, UriKind.Relative),
                                        UriBaseId = uriBaseId
                                     }
                                }
                            }
                        }
                    }
                }
            };

            var visitor = new InsertOptionalDataVisitor(OptionallyEmittedData.TextFiles);
            visitor.VisitRun(run);

            run.OriginalUriBaseIds.Should().BeNull();
            run.Artifacts.Count.Should().Be(1);
            run.Artifacts[0].Contents.Should().BeNull();

            visitor = new InsertOptionalDataVisitor(OptionallyEmittedData.TextFiles, originalUriBaseIds);
            visitor.VisitRun(run);

            run.OriginalUriBaseIds.Should().Equal(originalUriBaseIds);

            run.Artifacts[0].Contents?.Text?.Should().NotBeNull();
            run.Artifacts[0].Contents.Text.Should().Be(inputFileText);
        }
    }
}
