using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFileMetadataStruct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FilePath",
                table: "MangaFile",
                newName: "FileMetadata_Path");

            migrationBuilder.AddColumn<string>(
                name: "FileMetadata_CoverFile",
                table: "MangaFile",
                type: "TEXT",
                nullable: true);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileMetadata_CoverFile",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "FileMetadata_FileSize",
                table: "MangaFile");

            migrationBuilder.DropColumn(
                name: "FileMetadata_PageRange",
                table: "MangaFile");

            migrationBuilder.RenameColumn(
                name: "FileMetadata_Path",
                table: "MangaFile",
                newName: "FilePath");
        }
    }
}
