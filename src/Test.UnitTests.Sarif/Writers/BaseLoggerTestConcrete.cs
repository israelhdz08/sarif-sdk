﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

using Microsoft.CodeAnalysis.Sarif;
using Microsoft.CodeAnalysis.Sarif.Writers;

namespace Microsoft.CodeAnalysis.Test.UnitTests.Sarif.Writers
{
    public class BaseLoggerTestConcrete : BaseLogger
    {
        public BaseLoggerTestConcrete(FailureLevelSet failureLevels,
                                      ResultKindSet resultKinds) : base(failureLevels, resultKinds) { }
    }
}
