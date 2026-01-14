using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParseChaptersFromVolume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MangaFile_FilePath",
                table: "MangaFile");

            migrationBuilder.RenameColumn(
                name: "FilePath",
                table: "MangaFile",
                newName: "FileMetadata_Path");

            migrationBuilder.AddColumn<int>(
                name: "FileMetadata_CoverIndex",
                table: "MangaFile",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "FileMetadata_FileSize",
                table: "MangaFile",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "FileMetadata_PageRange",
                table: "MangaFile",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ParseChaptersFromVolumes",
                table: "Library",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MangaFile_FileMetadata_Path",
                table: "MangaFile",
                column: "FileMetadata_Path");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MangaFile_FileMetadata_Path",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "FileMetadata_CoverIndex",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "FileMetadata_FileSize",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "FileMetadata_PageRange",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "ParseChaptersFromVolumes",
                table: "Library");

            migrationBuilder.RenameColumn(
                name: "FileMetadata_Path",
                table: "MangaFile",
                newName: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_MangaFile_FilePath",
                table: "MangaFile",
                column: "FilePath");
        }
    }
}
