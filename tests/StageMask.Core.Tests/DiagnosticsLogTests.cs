using System;
using System.IO;
using System.Linq;
using StageMask.Core.Diagnostics;
using StageMask.Core.Settings;
using Xunit;

namespace StageMask.Core.Tests;

/// <summary>診断ログ(CSV)のテスト。</summary>
public class DiagnosticsLogTests
{
    private static string NewDir() => Path.Combine(Path.GetTempPath(), "stagemask-diag-" + Guid.NewGuid());

    [Fact]
    public void Row_HasSameNumberOfColumnsAsHeader()
    {
        var row = DiagnosticsLog.FormatRow(new DiagnosticsSample { Time = DateTime.UtcNow, CpuPercent = 12.345, Events = "パニックON" });
        Assert.Equal(DiagnosticsLog.Columns.Length, row.Split(',').Length);
        Assert.Contains("12.3", row);
    }

    [Fact]
    public void Numbers_UseDotRegardlessOfCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var row = DiagnosticsLog.FormatRow(new DiagnosticsSample { Time = DateTime.UtcNow, WorkingSetMb = 1.5 });
            Assert.Contains("1.5", row);
            Assert.DoesNotContain("1,5", row);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("-5", "'-5")]
    [InlineData("記録開始", "記録開始")]
    public void Escape_IsCsvSafe(string input, string expected)
    {
        Assert.Equal(expected, DiagnosticsLog.Escape(input));
    }

    [Fact]
    public void Append_WritesHeaderOnceAndRows()
    {
        var dir = NewDir();
        try
        {
            var log = new DiagnosticsLog(dir);
            log.Append(new DiagnosticsSample { Time = DateTime.UtcNow });
            log.Append(new DiagnosticsSample { Time = DateTime.UtcNow });
            var lines = File.ReadAllLines(log.CurrentPath!);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("time,", lines[0].TrimStart('﻿'));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Rotation_KeepsOnlyMaxFiles()
    {
        var dir = NewDir();
        try
        {
            var log = new DiagnosticsLog(dir, maxBytesPerFile: 1024, maxFiles: 3);
            var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 200; i++)
                log.Append(new DiagnosticsSample { Time = t.AddSeconds(i), Events = new string('x', 50) });
            var files = Directory.GetFiles(dir, "diag-*.csv");
            Assert.True(files.Length <= 3, $"files={files.Length}");
            Assert.Contains(log.CurrentPath!, files);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Rotate_StartsNewFile()
    {
        var dir = NewDir();
        try
        {
            var log = new DiagnosticsLog(dir);
            log.Append(new DiagnosticsSample { Time = DateTime.UtcNow });
            var first = log.CurrentPath;
            log.Rotate();
            log.Append(new DiagnosticsSample { Time = DateTime.UtcNow });
            Assert.NotEqual(first, log.CurrentPath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Settings_DiagnosticsIsOffByDefault_AndOldFileGetsDefaults()
    {
        Assert.False(new AppSettings().Diagnostics.Enabled);
        var dir = NewDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"Version\":1}");
            var s = new SettingsStore(path).Load();
            Assert.NotNull(s.Diagnostics);
            Assert.False(s.Diagnostics.Enabled);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
