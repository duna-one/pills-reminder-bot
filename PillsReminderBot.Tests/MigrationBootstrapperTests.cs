using PillsReminderBot.Persistence;

namespace PillsReminderBot.Tests;

public sealed class MigrationBootstrapperTests
{
    [Fact]
    public void GetBaselineAction_EmptyDatabase_RunsMigrationsNormally()
    {
        var action = MigrationBootstrapper.GetBaselineAction(
            initialMigrationApplied: false,
            remindersTableExists: false,
            userProfilesTableExists: false);

        Assert.Equal(MigrationBaselineAction.RunMigrations, action);
    }

    [Fact]
    public void GetBaselineAction_DatabaseWithInitialMigrationApplied_RunsMigrationsNormally()
    {
        var action = MigrationBootstrapper.GetBaselineAction(
            initialMigrationApplied: true,
            remindersTableExists: true,
            userProfilesTableExists: true);

        Assert.Equal(MigrationBaselineAction.RunMigrations, action);
    }

    [Fact]
    public void GetBaselineAction_LegacyEnsureCreatedSchema_BaselinesInitialMigration()
    {
        var action = MigrationBootstrapper.GetBaselineAction(
            initialMigrationApplied: false,
            remindersTableExists: true,
            userProfilesTableExists: true);

        Assert.Equal(MigrationBaselineAction.BaselineInitialMigration, action);
    }

    [Fact]
    public void GetBaselineAction_HistoryTableWithoutInitialMigrationAndLegacySchema_BaselinesInitialMigration()
    {
        var action = MigrationBootstrapper.GetBaselineAction(
            initialMigrationApplied: false,
            remindersTableExists: true,
            userProfilesTableExists: true);

        Assert.Equal(MigrationBaselineAction.BaselineInitialMigration, action);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void GetBaselineAction_PartialLegacySchema_RequiresManualRepair(bool remindersTableExists, bool userProfilesTableExists)
    {
        var action = MigrationBootstrapper.GetBaselineAction(
            initialMigrationApplied: false,
            remindersTableExists: remindersTableExists,
            userProfilesTableExists: userProfilesTableExists);

        Assert.Equal(MigrationBaselineAction.PartialLegacySchema, action);
    }
}
