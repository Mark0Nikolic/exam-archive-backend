using ExamArchive.Models;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Data;

public class ExamArchiveDbContext : DbContext
{
    public ExamArchiveDbContext(DbContextOptions<ExamArchiveDbContext> options)
        : base(options)
    {
    }

    public DbSet<Studies> Studies => Set<Studies>();
    public DbSet<Major> Majors => Set<Major>();
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<MajorSubject> MajorSubjects => Set<MajorSubject>();
    public DbSet<Paper> Papers => Set<Paper>();
    public DbSet<PaperFile> PaperFiles => Set<PaperFile>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Studies>(entity =>
        {
            entity.Property(s => s.NameSr)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(s => s.NameEn)
                .HasMaxLength(100);

            entity.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_Studies_YearsOfStudy",
                    "`YearsOfStudy` >= 1");
            });

            entity.HasData(
                new Studies { Id = 1, NameSr = "Основне академске студије", NameEn = "Bachelor's", YearsOfStudy = 3 },
                new Studies { Id = 2, NameSr = "Мастер академске студије", NameEn = "Master's", YearsOfStudy = 2 });
        });

        modelBuilder.Entity<Major>(entity =>
        {
            entity.Property(m => m.NameSr)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(m => m.NameEn)
                .HasMaxLength(200);

            // Restrict: a Studies level cannot be deleted while majors still reference it.
            entity.HasOne(m => m.Studies)
                .WithMany(s => s.Majors)
                .HasForeignKey(m => m.StudiesId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Subject>(entity =>
        {
            entity.Property(s => s.NameSr)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(s => s.NameEn)
                .HasMaxLength(200);

            entity.Property(s => s.Code)
                .HasMaxLength(20);

            // Rows without a code are exempt: NULLs are distinct from one another in
            // a unique index, which is what allows subjects to exist before their
            // code is known.
            entity.HasIndex(s => s.Code)
                .IsUnique();
        });

        modelBuilder.Entity<MajorSubject>(entity =>
        {
            entity.HasKey(ms => new { ms.MajorId, ms.SubjectId });

            // Cascade: the link row has no meaning once either side is gone.
            entity.HasOne(ms => ms.Major)
                .WithMany(m => m.MajorSubjects)
                .HasForeignKey(ms => ms.MajorId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ms => ms.Subject)
                .WithMany(s => s.MajorSubjects)
                .HasForeignKey(ms => ms.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable(t =>
            {
                // Floor only: the ceiling is Studies.YearsOfStudy, which the API
                // enforces, because a check constraint here cannot see that column.
                t.HasCheckConstraint(
                    "CK_MajorSubject_YearOfStudy",
                    "`YearOfStudy` >= 1");
            });
        });

        modelBuilder.Entity<Paper>(entity =>
        {
            entity.Property(p => p.ExamType)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(20);

            // Stored as the enum's name, not its number: it keeps the CK_Paper_Status
            // constraint working and leaves the table readable in a database client.
            entity.Property(p => p.Status)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(PaperStatus.Pending);

            // UTC_TIMESTAMP() rather than CURRENT_TIMESTAMP, which in MySQL is the
            // session time zone — on a machine in Belgrade every default-stamped row
            // would be two hours in the future. Parenthesised because MySQL 8.0.13+
            // requires that form for an expression default.
            //
            // The conversion on the way out is what makes the UTC claim true: a MySQL
            // datetime carries no zone, so the value reads back as Unspecified and
            // System.Text.Json writes it without a trailing Z.
            entity.Property(p => p.UploadedAt)
                .HasDefaultValueSql("(UTC_TIMESTAMP())")
                .HasConversion(
                    value => value,
                    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

            // Same UTC treatment as UploadedAt, and for the same reason.
            entity.Property(p => p.ReviewedAt)
                .HasConversion(
                    value => value,
                    value => value.HasValue
                        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                        : value);

            entity.Property(p => p.RejectionReason)
                .HasMaxLength(500);

            // Restrict: a subject cannot be deleted while it still has papers.
            entity.HasOne(p => p.Subject)
                .WithMany(s => s.Papers)
                .HasForeignKey(p => p.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);

            // SetNull, not Cascade: removing an account must not remove the papers it
            // contributed.
            entity.HasOne(p => p.SubmittedBy)
                .WithMany(u => u.Papers)
                .HasForeignKey(p => p.SubmittedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Common lookup: papers for a subject, newest first.
            entity.HasIndex(p => new { p.SubjectId, p.Year, p.Month });

            // Backs "my submissions".
            entity.HasIndex(p => p.SubmittedByUserId);

            entity.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_Paper_Month",
                    "`Month` >= 1 AND `Month` <= 12");

                t.HasCheckConstraint(
                    "CK_Paper_ExamType",
                    "`ExamType` IN ('Midterm', 'Final', 'Resit')");

                t.HasCheckConstraint(
                    "CK_Paper_Status",
                    "`Status` IN ('Pending', 'Approved', 'Rejected')");

                // One-directional on purpose: it forbids a reason on a paper that is
                // not rejected, but does not demand one on papers that are, because
                // rows decided before this column existed have none.
                t.HasCheckConstraint(
                    "CK_Paper_RejectionReason",
                    "`Status` = 'Rejected' OR `RejectionReason` IS NULL");

                t.HasCheckConstraint(
                    "CK_Paper_ReviewedAt",
                    "`Status` <> 'Pending' OR `ReviewedAt` IS NULL");
            });
        });

        modelBuilder.Entity<PaperFile>(entity =>
        {
            entity.Property(f => f.StoredPath)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(f => f.ContentType)
                .IsRequired()
                .HasMaxLength(100);

            // Cascade deletes rows, not the bytes on disk — whatever removes a paper
            // is responsible for the files, or they leak.
            entity.HasOne(f => f.Paper)
                .WithMany(p => p.Files)
                .HasForeignKey(f => f.PaperId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique rather than merely indexed: two rows claiming the same page of
            // the same paper would make the reading order ambiguous.
            entity.HasIndex(f => new { f.PaperId, f.PageNumber })
                .IsUnique();

            entity.ToTable(t =>
            {
                // Built from PaperFileTypes so the constraint cannot drift from the
                // validation. Adding a format there registers as a model change.
                var contentTypes = string.Join(
                    ", ",
                    PaperFileTypes.All.Select(type => $"'{type.ContentType}'"));

                t.HasCheckConstraint(
                    "CK_PaperFile_ContentType",
                    $"`ContentType` IN ({contentTypes})");

                t.HasCheckConstraint(
                    "CK_PaperFile_PageNumber",
                    "`PageNumber` >= 1");

                // Zero is permitted and means "size not recorded": rows migrated from
                // the single-FilePath schema predate size tracking.
                t.HasCheckConstraint(
                    "CK_PaperFile_SizeBytes",
                    "`SizeBytes` >= 0");
            });
        });

        modelBuilder.Entity<User>(entity =>
        {
            // A _ci collation makes both the comparison and the unique index
            // case-insensitive, so "Marko" and "marko" cannot coexist. Named
            // explicitly rather than left to the server default, because a column
            // whose collation differs from the ones it is compared against raises
            // "illegal mix of collations". It is also accent-insensitive, which
            // SQLite's NOCASE was not.
            entity.Property(u => u.Username)
                .IsRequired()
                .HasMaxLength(50)
                .UseCollation("utf8mb4_0900_ai_ci");

            entity.HasIndex(u => u.Username)
                .IsUnique();

            entity.Property(u => u.PasswordHash)
                .IsRequired()
                .HasMaxLength(256);

            // No string conversion, unlike Status and ExamType: this column stores
            // the enum's number.
            entity.Property(u => u.Role)
                .IsRequired();

            entity.Property(u => u.IsActive)
                .HasDefaultValue(true);

            // False for every existing row: accounts that predate this column chose
            // their own passwords.
            entity.Property(u => u.MustChangePassword)
                .HasDefaultValue(false);

            entity.Property(u => u.CreatedAt)
                .HasDefaultValueSql("(UTC_TIMESTAMP())")
                .HasConversion(
                    value => value,
                    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

            entity.ToTable(t =>
            {
                // Zero is absent on purpose: it is what an omitted role binds to, so
                // an account can never be stored with one nobody chose.
                t.HasCheckConstraint(
                    "CK_User_Role",
                    "`Role` IN (1, 2, 3, 4)");
            });
        });
    }
}
