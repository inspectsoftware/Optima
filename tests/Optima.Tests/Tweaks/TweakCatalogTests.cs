using Optima.Core.Models;
using Xunit;

namespace Optima.Tests.Tweaks;

public sealed class TweakCatalogTests
{
    [Fact]
    public void Ids_are_unique_and_stable_slugs()
    {
        var ids = TweakCatalog.All.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9-]+$", id));
    }

    [Fact]
    public void Every_tweak_carries_full_disclosure_text()
    {
        Assert.All(TweakCatalog.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Category));
            Assert.False(string.IsNullOrWhiteSpace(t.WhatItChanges));
            Assert.False(string.IsNullOrWhiteSpace(t.PotentialBenefit));
            Assert.False(string.IsNullOrWhiteSpace(t.PotentialDownside));
            Assert.NotEmpty(t.Values);
        });
    }

    [Fact]
    public void Dword_data_parses_as_unsigned()
    {
        var dwords = TweakCatalog.All.SelectMany(t => t.Values).Where(v => v.Kind == TweakValueKind.Dword);
        Assert.All(dwords, v =>
        {
            Assert.True(uint.TryParse(v.EnabledData, out _), $"{TweakCatalog.ValueKey(v)}: '{v.EnabledData}'");
            if (v.DefaultData is not null)
            {
                Assert.True(uint.TryParse(v.DefaultData, out _), $"{TweakCatalog.ValueKey(v)}: '{v.DefaultData}'");
            }
        });
    }

    [Fact]
    public void Key_paths_are_relative_hive_paths()
    {
        Assert.All(TweakCatalog.All.SelectMany(t => t.Values), v =>
        {
            Assert.False(v.KeyPath.StartsWith(@"\", StringComparison.Ordinal));
            Assert.DoesNotContain("HKEY", v.KeyPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(v.ValueName));
        });
    }

    [Fact]
    public void Value_keys_are_unique_within_each_tweak()
    {
        Assert.All(TweakCatalog.All, t =>
        {
            var keys = t.Values.Select(TweakCatalog.ValueKey).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        });
    }

    [Fact]
    public void Elevation_flag_matches_hives()
    {
        Assert.All(TweakCatalog.All, t =>
            Assert.Equal(t.Values.Any(v => v.Hive == TweakHive.LocalMachine), t.RequiresElevation));
    }

    [Fact]
    public void Find_resolves_every_id_and_rejects_unknown()
    {
        Assert.All(TweakCatalog.All, t => Assert.Same(t, TweakCatalog.Find(t.Id)));
        Assert.Null(TweakCatalog.Find("not-a-tweak"));
    }
}
