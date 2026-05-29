using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PillsReminderBot.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderWeekDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WeekDaysMask",
                table: "Reminders",
                type: "integer",
                nullable: false,
                defaultValue: 127);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WeekDaysMask",
                table: "Reminders");
        }
    }
}
