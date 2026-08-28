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

            entity.HasData(
                new Studies { Id = 1, NameSr = "Основне академске студије", NameEn = "Bachelor's" },
                new Studies { Id = 2, NameSr = "Мастер академске студије", NameEn = "Master's" });
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

            // Unique, so the database itself rejects a duplicate code no matter
            // what inserts it. A code that can repeat is not an identifier, just a
            // second name. Rows without a code are exempt: SQLite treats NULLs as
            // distinct from one another in a unique index, which is what allows
            // subjects to exist before their code is known.
            entity.HasIndex(s => s.Code)
                .IsUnique();
        });

        modelBuilder.Entity<MajorSubject>(entity =>
        {
            // Composite primary key — no surrogate Id column.
            entity.HasKey(ms => new { ms.MajorId, ms.SubjectId });

            // Cascade: deleting either side removes the link row, which has no
            // meaning on its own.
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
                // Six covers a 3-4 year bachelor's and a 1-2 year master's. Without
                // this the column accepts year 47 or -3 as readily as year 2.
                t.HasCheckConstraint(
                    "CK_MajorSubject_YearOfStudy",
                    "`YearOfStudy` >= 1 AND `YearOfStudy` <= 6");
            });
        });

        modelBuilder.Entity<Paper>(entity =>
        {
            // Stored as the enum's name for the same reasons as Status below.
            entity.Property(p => p.ExamType)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(20);

            // Stored as the enum's name, not its number: it keeps the existing
            // text column and CK_Paper_Status constraint working, and leaves the
            // table readable by eye in a SQLite browser.
            entity.Property(p => p.Status)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(PaperStatus.Pending);

            // Filled in by the database on insert; stored as UTC.
            //
            // UTC_TIMESTAMP() rather than CURRENT_TIMESTAMP, which is the whole
            // point of this line. SQLite's CURRENT_TIMESTAMP is UTC, MySQL's is the
            // session time zone, and nothing announces the difference: on a machine
            // in Belgrade every default-stamped row would simply be two hours in the
            // future, and the conversion below would then label it "Z" and make the
            // wrong answer look authoritative. Parenthesised because MySQL 8.0.13+
            // requires that form for any default that is an expression rather than
            // the CURRENT_TIMESTAMP special case.
            //
            // The conversion on the way out is what makes the UTC claim true in
            // practice. A MySQL datetime carries no zone, so a DateTime read back
            // arrives with Kind = Unspecified, and System.Text.Json then writes it
            // without a trailing Z. A browser parsing "2026-08-14T11:42:47" treats
            // it as local time, so every timestamp was landing hours off. Stamping
            // the kind on read makes the serialized form say what the column means.
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

            // Hex SHA-256, so always exactly 64 characters when present.
            entity.Property(p => p.ClaimTokenHash)
                .HasMaxLength(64);

            // Unique, and the index is what makes the lookup a single seek rather
            // than a scan of every paper on each status check. NULLs are distinct
            // from one another in SQLite, so the many papers without a code — staff
            // uploads and everything predating this column — are exempt.
            entity.HasIndex(p => p.ClaimTokenHash)
                .IsUnique();

            // Restrict: a subject cannot be deleted while it still has papers.
            entity.HasOne(p => p.Subject)
                .WithMany(s => s.Papers)
                .HasForeignKey(p => p.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);

            // SetNull, not Cascade: removing an account must not remove the papers
            // it contributed. They stay in the archive, anonymous — which is the
            // state most rows are in anyway.
            entity.HasOne(p => p.SubmittedBy)
                .WithMany(u => u.Papers)
                .HasForeignKey(p => p.SubmittedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Common lookup: papers for a subject, newest first.
            entity.HasIndex(p => new { p.SubjectId, p.Year, p.Month });

            // Backs "my submissions". Anonymous rows are the majority and all carry
            // NULL here, so the index stays small relative to the table.
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

                // One-directional on purpose. It forbids a reason on a paper that
                // is not rejected — which would contradict the status — but does
                // not demand one on papers that are, because rows decided before
                // this column existed have no reason and inventing one would put
                // fiction in the database. New rejections are required to carry a
                // reason by the API instead.
                t.HasCheckConstraint(
                    "CK_Paper_RejectionReason",
                    "`Status` = 'Rejected' OR `RejectionReason` IS NULL");

                // Likewise: a paper still waiting cannot have been reviewed.
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

            // Cascade: a file has no meaning once its paper is gone. Note this
            // deletes rows, not the bytes on disk — whatever removes a paper is
            // responsible for the files, or they leak.
            entity.HasOne(f => f.Paper)
                .WithMany(p => p.Files)
                .HasForeignKey(f => f.PaperId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique rather than merely indexed: two rows claiming the same page
            // of the same paper would make the reading order ambiguous, and the
            // database is the only place that can actually rule it out.
            entity.HasIndex(f => new { f.PaperId, f.PageNumber })
                .IsUnique();

            entity.ToTable(t =>
            {
                // Built from PaperFileTypes so the constraint cannot drift from the
                // validation. Adding a format there registers as a model change and
                // EF will ask for a migration, which is the intended nudge.
                var contentTypes = string.Join(
                    ", ",
                    PaperFileTypes.All.Select(type => $"'{type.ContentType}'"));

                t.HasCheckConstraint(
                    "CK_PaperFile_ContentType",
                    $"`ContentType` IN ({contentTypes})");

                t.HasCheckConstraint(
                    "CK_PaperFile_PageNumber",
                    "`PageNumber` >= 1");

                // Zero is permitted and means "size not recorded" — rows migrated
                // from the single-FilePath schema predate size tracking, and SQL
                // cannot measure a file on disk to backfill them. Uploads always
                // write a real size, so only historical rows carry 0.
                t.HasCheckConstraint(
                    "CK_PaperFile_SizeBytes",
                    "`SizeBytes` >= 0");
            });
        });

        modelBuilder.Entity<User>(entity =>
        {
            // A _ci collation makes both the comparison and the unique index below
            // case-insensitive, so "Marko" and "marko" cannot coexist as separate
            // accounts and either spelling finds the same row at login. The
            // alternative — a second NormalizedUsername column kept in sync by the
            // application — is one more thing to forget to update.
            //
            // Named explicitly rather than left to the server default, so the column
            // does not silently change meaning on a server configured with a
            // different one. It is spelled out as MySQL 8.0's own default because a
            // column whose collation differs from the ones it is compared against
            // raises "illegal mix of collations" rather than simply comparing.
            //
            // This is also accent-insensitive, which SQLite's NOCASE was not: "márko"
            // now collides with "marko". For a login name that is a fair trade, since
            // two accounts separated only by an accent are more likely impersonation
            // than intent.
            entity.Property(u => u.Username)
                .IsRequired()
                .HasMaxLength(50)
                .UseCollation("utf8mb4_0900_ai_ci");

            entity.HasIndex(u => u.Username)
                .IsUnique();

            // Long enough for the current PBKDF2 format with room for a future one.
            entity.Property(u => u.PasswordHash)
                .IsRequired()
                .HasMaxLength(256);

            entity.Property(u => u.Role)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(20);

            entity.Property(u => u.IsActive)
                .HasDefaultValue(true);

            // False for every existing row: accounts that predate this column chose
            // their own passwords, so demanding a change would lock out working
            // logins to fix a problem they do not have.
            entity.Property(u => u.MustChangePassword)
                .HasDefaultValue(false);

            // UTC_TIMESTAMP() for the same reason as Paper.UploadedAt.
            entity.Property(u => u.CreatedAt)
                .HasDefaultValueSql("(UTC_TIMESTAMP())")
                .HasConversion(
                    value => value,
                    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

            entity.ToTable(t =>
            {
                // The authorization policies name these strings. A typo that put
                // 'Moderater' in the column would produce an account that passes
                // login and silently fails every [Authorize(Roles = ...)] check,
                // which is a confusing way to find a spelling mistake.
                t.HasCheckConstraint(
                    "CK_User_Role",
                    "`Role` IN ('User', 'Moderator', 'Admin')");
            });
        });
    }
}
