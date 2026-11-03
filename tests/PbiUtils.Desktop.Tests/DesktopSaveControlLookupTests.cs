// FilePath    : pbi-utils\tests\PbiUtils.Desktop.Tests\DesktopSaveControlLookupTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 失效UIA候选、有界重扫和未知Invoke无重放边界, 不操作真实窗口.

using PbiUtils.Windows.Desktop.Files;
using System.Windows.Automation;
using PbiUtils.Core;
using PbiUtils.Windows;
using Xunit;

namespace PbiUtils.Desktop.Tests;

public sealed class DesktopSaveControlLookupTests
{
    [Fact]
    public void DisappearingUnrelatedCandidateDoesNotHideSaveControl()
    {
        Assert.Equal([2], DesktopSaveControlLookup.Available([1, 2, 3], value => value == 1 ? throw new ElementNotAvailableException() : value == 2));
        Assert.Equal([2, 3], DesktopSaveControlLookup.Available([1, 2, 3], value => value > 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RescansOnlyLookupWithFreshIdentityCheck(bool transient)
    {
        var identities = 0; var reads = 0;
        Task Verify(CancellationToken token) { identities++; return Task.CompletedTask; }
        Task<string> Read(CancellationToken token)
        {
            reads++; if (reads == 1 || !transient) throw new ElementNotAvailableException(); return Task.FromResult("unique-save");
        }
        if (transient) Assert.Equal("unique-save", await DesktopSaveControlLookup.FindAsync(Verify, Read, TimeSpan.Zero, default));
        else Assert.Equal("DESKTOP_SAVE_UNAVAILABLE", (await Assert.ThrowsAsync<AppException>(() => DesktopSaveControlLookup.FindAsync(Verify, Read, TimeSpan.Zero, default))).Code);
        Assert.Equal(transient ? 2 : 3, reads); Assert.Equal(reads, identities);
    }

    [Fact]
    public async Task AmbiguousControlNeverBecomesSuccessAndIdentityChangeStopsScan()
    {
        var reads = 0;
        await Assert.ThrowsAsync<AppException>(() => DesktopSaveControlLookup.FindAsync<string>(_ => throw new AppException("DESKTOP_FILE_CHANGED", "changed"),
            _ => { reads++; return Task.FromResult("other-file"); }, TimeSpan.Zero, default)); Assert.Equal(0, reads);
        Assert.Equal("DESKTOP_SAVE_UNAVAILABLE", (await Assert.ThrowsAsync<AppException>(() => DesktopSaveControlLookup.FindAsync<string>(_ => Task.CompletedTask,
            _ => throw new AppException("DESKTOP_SAVE_UNAVAILABLE", "ambiguous"), TimeSpan.Zero, default))).Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownInvokeIsOnlyObservedAndNeverReplayed(bool persisted)
    {
        var invokes = 0; var reads = 0;
        Task Invoke() { invokes++; throw new ElementNotAvailableException(); }
        Task<bool> Confirm(CancellationToken token) { reads++; return Task.FromResult(persisted); }
        if (persisted) Assert.True(await DesktopSaveControlLookup.InvokeOnceAsync(Invoke, Confirm, default));
        else await Assert.ThrowsAsync<ElementNotAvailableException>(() => DesktopSaveControlLookup.InvokeOnceAsync(Invoke, Confirm, default));
        Assert.Equal(1, invokes); Assert.Equal(1, reads);
    }
}
