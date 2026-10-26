/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Infrastructure.Updates;

namespace Launcher.Tests.Infrastructure.Updates;

public sealed class LauncherUpdateApplyLoggerTests : TestTempDirectory
{
    [Fact]
    public void IndependentUpdaterLogOmitsExceptionMessageAndUrlSecrets()
    {
        var logger = new LauncherUpdateApplyLogger(TempRoot);
        logger.Info("Source=https://example.test/signed/path-secret?token=query-secret");
        logger.Error("Apply failed.", new InvalidOperationException("unlabelled-exception-secret"));

        var output = File.ReadAllText(Assert.Single(Directory.GetFiles(TempRoot, "updater-*.log")));
        Assert.Contains("ErrorType=InvalidOperationException", output, StringComparison.Ordinal);
        Assert.DoesNotContain("path-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("unlabelled-exception-secret", output, StringComparison.Ordinal);
    }
}
