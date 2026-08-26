using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ombi.Store.Context.Postgres;

#nullable disable

namespace Ombi.Store.Migrations.OmbiPostgres
{
    [DbContext(typeof(OmbiPostgresContext))]
    [Migration("20260826000100_AddRequestQualityProfileOverrides")]
    public partial class AddRequestQualityProfileOverrides : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QualityOverride4K",
                table: "MovieRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QualityOverride",
                table: "ChildRequests",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QualityOverride4K",
                table: "MovieRequests");

            migrationBuilder.DropColumn(
                name: "QualityOverride",
                table: "ChildRequests");
        }
    }
}
