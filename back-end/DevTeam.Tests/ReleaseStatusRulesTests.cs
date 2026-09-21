using DevTeam.Broker.Domain;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Tests;

public class ReleaseStatusRulesTests
{
    private static ReleaseFeatureStatus[] Features(params ReleaseFeatureStatus[] s) => s;

    [Fact]
    public void Ready_WithANewInProgressFeature_IsInProgress()
    {
        // The reported bug: "adding" finished (release stored Ready), then "subtraction" was added.
        var effective = ReleaseStatusRules.Effective(
            ReleaseStatus.Ready, Features(ReleaseFeatureStatus.Complete, ReleaseFeatureStatus.InProgress));

        Assert.Equal(ReleaseStatus.InProgress, effective);
    }

    [Fact]
    public void Ready_WhenEveryFeatureIsComplete_StaysReady()
    {
        Assert.Equal(ReleaseStatus.Ready,
            ReleaseStatusRules.Effective(ReleaseStatus.Ready, Features(ReleaseFeatureStatus.Complete, ReleaseFeatureStatus.Complete)));
    }

    [Theory]
    [InlineData(ReleaseFeatureStatus.OnHold)]
    [InlineData(ReleaseFeatureStatus.Proposed)]
    [InlineData(ReleaseFeatureStatus.InProgress)]
    public void Ready_WithAnyUnfinishedFeature_IsInProgress(ReleaseFeatureStatus unfinished)
    {
        Assert.Equal(ReleaseStatus.InProgress,
            ReleaseStatusRules.Effective(ReleaseStatus.Ready, Features(ReleaseFeatureStatus.Complete, unfinished)));
    }

    [Fact]
    public void Ready_WithNoFeatures_IsNotReadyToShip()
    {
        Assert.Equal(ReleaseStatus.InProgress, ReleaseStatusRules.Effective(ReleaseStatus.Ready, Features()));
    }

    [Theory]
    [InlineData(ReleaseStatus.Released)]
    [InlineData(ReleaseStatus.Cancelled)]
    [InlineData(ReleaseStatus.Draft)]
    [InlineData(ReleaseStatus.InProgress)]
    [InlineData(ReleaseStatus.Blocked)]
    [InlineData(ReleaseStatus.Escalated)]
    public void EveryOtherStoredStatus_IsUnchanged_WhateverTheFeaturesAre(ReleaseStatus stored)
    {
        Assert.Equal(stored, ReleaseStatusRules.Effective(stored, Features(ReleaseFeatureStatus.InProgress)));
        Assert.Equal(stored, ReleaseStatusRules.Effective(stored, Features(ReleaseFeatureStatus.Complete)));
        Assert.Equal(stored, ReleaseStatusRules.Effective(stored, Features()));
    }

    [Fact]
    public void Release_ExposesTheDerivedValueWithoutChangingTheStoredOne()
    {
        var release = new DevTeamRelease { Status = ReleaseStatus.Ready };
        release.Features.Add(new ReleaseFeature { Key = "adding", Status = ReleaseFeatureStatus.Complete });
        release.Features.Add(new ReleaseFeature { Key = "subtraction", Status = ReleaseFeatureStatus.InProgress });

        Assert.Equal(ReleaseStatus.InProgress, release.EffectiveStatus);
        Assert.Equal(ReleaseStatus.Ready, release.Status); // stored value untouched
    }

    [Fact]
    public void EffectiveStatus_IsNotAColumn()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new DevTeamDbContext(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);

        var entity = db.Model.FindEntityType(typeof(DevTeamRelease))!;

        Assert.Null(entity.FindProperty(nameof(DevTeamRelease.EffectiveStatus)));
    }
}
