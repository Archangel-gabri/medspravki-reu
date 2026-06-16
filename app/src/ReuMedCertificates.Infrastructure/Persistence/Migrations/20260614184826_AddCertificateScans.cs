using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReuMedCertificates.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCertificateScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certificate_scans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CertificateId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StoredName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecognitionStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RecognitionJson = table.Column<string>(type: "jsonb", nullable: true),
                    RecognitionModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecognizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificate_scans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_certificate_scans_medical_certificates_CertificateId",
                        column: x => x.CertificateId,
                        principalTable: "medical_certificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_certificate_scans_students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_certificate_scans_CertificateId",
                table: "certificate_scans",
                column: "CertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_certificate_scans_StudentId",
                table: "certificate_scans",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certificate_scans");
        }
    }
}
