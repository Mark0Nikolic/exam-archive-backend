using ExamArchive.Models;
using ExamArchive.Services;
using Microsoft.EntityFrameworkCore;

namespace ExamArchive.Data;

// Development-only sample data. Kept out of the migrations so it never reaches a
// real deployment, and written in plain EF Core so it survives a provider change.
public static class SeedData
{
    // A map rather than inline data so it can serve twice: creating subjects on an
    // empty database, and backfilling ones seeded before the Code column existed.
    private static readonly (string Code, string NameSr, string NameEn)[] SubjectCatalogue =
    [
        ("MAT101", "Математика I", "Mathematics I"),
        ("MAT120", "Линеарна алгебра", "Linear Algebra"),
        ("MAT210", "Статистика", "Statistics"),
        ("IT101", "Основе програмирања", "Programming Fundamentals"),
        ("IT230", "Структуре података и алгоритми", "Data Structures and Algorithms"),
        ("IT240", "Базе података", "Databases"),
        ("IT310", "Оперативни системи", "Operating Systems"),
        ("IT320", "Рачунарске мреже", "Computer Networks"),
        ("IT330", "Архитектура софтвера", "Software Architecture"),
        ("IT410", "Машинско учење", "Machine Learning"),
        ("IT450", "Криптографија", "Cryptography"),
        ("EE110", "Дигитална електроника", "Digital Electronics"),
    ];

    private static readonly (string NameSr, string NameEn)[] MajorCatalogue =
    [
        ("Рачунарске науке", "Computer Science"),
        ("Софтверско инжењерство", "Software Engineering"),
        ("Електротехника", "Electrical Engineering"),
        ("Наука о подацима", "Data Science"),
        ("Сајбер безбедност", "Cybersecurity"),
    ];

    // Not a secret: this file runs only from the Development branch in Program.cs,
    // so these accounts exist nowhere else. Deliberately shorter than
    // UserAccountService.MinimumPasswordLength, which is checked where a password is
    // set through the API and never at sign-in, so signing in with it works.
    private const string DevPassword = "password";

    private static readonly (string Username, UserRole Role)[] DevAccounts =
    [
        ("superadmin", UserRole.SuperAdmin),
        ("admin", UserRole.Admin),
        ("moderator", UserRole.Moderator),

        // Several students rather than one, because a submissions list with a single
        // author on every row cannot show whether a client reads the author at all.
        ("student", UserRole.User),
        ("milica", UserRole.User),
        ("stefan", UserRole.User),
        ("jovana", UserRole.User),
    ];

    public static async Task SeedAsync(
        ExamArchiveDbContext db,
        UserAccountService accounts,
        PaperFileStorage storage,
        ILogger logger)
    {
        // These run unconditionally, because a database that was already seeded is
        // exactly the one carrying the pre-localization names and missing the
        // accounts.
        await SeedUsersAsync(db, accounts, logger);
        await BackfillSubjectCodesAsync(db);
        await BackfillMajorNamesAsync(db);

        await SeedCatalogueAsync(db);
        await ClampSubjectYearsToStudyLengthAsync(db);
        await BackfillPaperSubmittersAsync(db, logger);

        // Last: it needs the papers above to exist, and it is what repairs a database
        // whose uploads folder was emptied.
        await WriteSampleFilesAsync(db, storage, logger);
    }

    private static async Task SeedCatalogueAsync(ExamArchiveDbContext db)
    {
        // Idempotent: if majors already exist, assume seeding has run.
        if (await db.Majors.AnyAsync())
        {
            return;
        }

        // Studies come from the migration's HasData, so look them up rather than insert.
        var bachelors = await db.Studies.SingleAsync(s => s.NameSr == "Основне академске студије");
        var masters = await db.Studies.SingleAsync(s => s.NameSr == "Мастер академске студије");

        var cs = new Major { NameSr = "Рачунарске науке", NameEn = "Computer Science", Studies = bachelors };
        var se = new Major { NameSr = "Софтверско инжењерство", NameEn = "Software Engineering", Studies = bachelors };
        var ee = new Major { NameSr = "Електротехника", NameEn = "Electrical Engineering", Studies = bachelors };
        var ds = new Major { NameSr = "Наука о подацима", NameEn = "Data Science", Studies = masters };
        var cy = new Major { NameSr = "Сајбер безбедност", NameEn = "Cybersecurity", Studies = masters };

        db.Majors.AddRange(cs, se, ee, ds, cy);

        static Subject Sub(string code)
        {
            var (_, nameSr, nameEn) = SubjectCatalogue.Single(entry => entry.Code == code);
            return new Subject { Code = code, NameSr = nameSr, NameEn = nameEn };
        }

        var math1 = Sub("MAT101");
        var linAlg = Sub("MAT120");
        var progFund = Sub("IT101");
        var dsa = Sub("IT230");
        var databases = Sub("IT240");
        var os = Sub("IT310");
        var networks = Sub("IT320");
        var ml = Sub("IT410");
        var crypto = Sub("IT450");
        var stats = Sub("MAT210");
        var digital = Sub("EE110");
        var arch = Sub("IT330");

        db.Subjects.AddRange(
            math1, linAlg, progFund, dsa, databases,
            os, networks, ml, crypto, stats, digital, arch);

        // Several subjects are shared across majors, and some are taught in a
        // different year depending on the major — that is what YearOfStudy is for.
        static MajorSubject Link(Major major, Subject subject, int year) =>
            new() { Major = major, Subject = subject, YearOfStudy = year };

        db.MajorSubjects.AddRange(
            Link(cs, math1, 1),
            Link(cs, progFund, 1),
            Link(cs, linAlg, 1),
            Link(cs, dsa, 2),
            Link(cs, databases, 2),
            Link(cs, os, 3),
            Link(cs, networks, 3),
            Link(cs, ml, 3),

            Link(se, math1, 1),
            Link(se, progFund, 1),
            Link(se, dsa, 2),
            Link(se, databases, 3),
            Link(se, arch, 3),

            Link(ee, math1, 1),
            Link(ee, digital, 1),
            Link(ee, stats, 2),
            Link(ee, os, 3),

            Link(ds, linAlg, 1),
            Link(ds, stats, 1),
            Link(ds, ml, 1),
            Link(ds, databases, 2),

            Link(cy, networks, 1),
            Link(cy, crypto, 1),
            Link(cy, os, 2),
            Link(cy, arch, 2));

        // Months follow a plausible academic calendar: midterms in Nov/Apr, finals
        // in Jan/Jun, resits in Sep.
        static Paper Doc(
            Subject subject,
            ExamType examType,
            int month,
            int year,
            PaperStatus status,
            DateTime uploadedAt,
            string? rejectionReason = null)
        {
            var slug = subject.Code!.ToLowerInvariant();
            return new Paper
            {
                Subject = subject,
                ExamType = examType,
                Month = month,
                Year = year,
                Status = status,
                UploadedAt = uploadedAt,

                // Decided a couple of days after upload, which also satisfies
                // CK_Paper_ReviewedAt.
                ReviewedAt = status == PaperStatus.Pending ? null : uploadedAt.AddDays(2),
                RejectionReason = status == PaperStatus.Rejected ? rejectionReason : null,

                Files =
                [
                    new PaperFile
                    {
                        StoredPath = $"/uploads/{year}/{slug}-{examType.ToString().ToLowerInvariant()}-{year}-{month:D2}.pdf",
                        ContentType = PaperFileTypes.Pdf.ContentType,
                        PageNumber = 1,

                        // Zero until the bytes are written, and the marker
                        // WriteSampleFilesAsync looks for.
                        SizeBytes = 0
                    }
                ]
            };
        }

        db.Papers.AddRange(
            Doc(databases, ExamType.Final, 1, 2024, PaperStatus.Approved, new DateTime(2024, 2, 3, 9, 14, 0, DateTimeKind.Utc)),
            Doc(databases, ExamType.Midterm, 11, 2024, PaperStatus.Approved, new DateTime(2024, 11, 28, 17, 2, 0, DateTimeKind.Utc)),
            Doc(databases, ExamType.Final, 1, 2025, PaperStatus.Approved, new DateTime(2025, 1, 30, 12, 45, 0, DateTimeKind.Utc)),
            Doc(databases, ExamType.Resit, 9, 2025, PaperStatus.Pending, new DateTime(2025, 9, 19, 20, 11, 0, DateTimeKind.Utc)),

            Doc(dsa, ExamType.Midterm, 11, 2024, PaperStatus.Approved, new DateTime(2024, 12, 1, 8, 30, 0, DateTimeKind.Utc)),
            Doc(dsa, ExamType.Final, 6, 2025, PaperStatus.Approved, new DateTime(2025, 6, 22, 15, 5, 0, DateTimeKind.Utc)),
            Doc(dsa, ExamType.Final, 6, 2024, PaperStatus.Rejected, new DateTime(2024, 7, 2, 11, 20, 0, DateTimeKind.Utc),
                "Pages 2 and 3 are too blurred to read. Please re-photograph in better light."),

            Doc(math1, ExamType.Midterm, 11, 2025, PaperStatus.Approved, new DateTime(2025, 11, 26, 10, 0, 0, DateTimeKind.Utc)),
            Doc(math1, ExamType.Final, 1, 2025, PaperStatus.Approved, new DateTime(2025, 2, 8, 13, 40, 0, DateTimeKind.Utc)),
            Doc(math1, ExamType.Resit, 9, 2024, PaperStatus.Approved, new DateTime(2024, 9, 25, 16, 55, 0, DateTimeKind.Utc)),
            Doc(math1, ExamType.Final, 1, 2026, PaperStatus.Pending, new DateTime(2026, 1, 29, 18, 12, 0, DateTimeKind.Utc)),

            Doc(os, ExamType.Final, 6, 2025, PaperStatus.Approved, new DateTime(2025, 6, 30, 9, 5, 0, DateTimeKind.Utc)),
            Doc(os, ExamType.Midterm, 4, 2025, PaperStatus.Pending, new DateTime(2025, 4, 14, 21, 33, 0, DateTimeKind.Utc)),

            Doc(ml, ExamType.Final, 6, 2025, PaperStatus.Approved, new DateTime(2025, 6, 18, 14, 22, 0, DateTimeKind.Utc)),
            Doc(ml, ExamType.Midterm, 4, 2026, PaperStatus.Pending, new DateTime(2026, 4, 21, 19, 47, 0, DateTimeKind.Utc)),

            Doc(crypto, ExamType.Final, 1, 2025, PaperStatus.Approved, new DateTime(2025, 1, 24, 10, 18, 0, DateTimeKind.Utc)),
            Doc(crypto, ExamType.Resit, 9, 2025, PaperStatus.Rejected, new DateTime(2025, 9, 8, 22, 4, 0, DateTimeKind.Utc),
                "This is the September 2024 paper, not 2025. Please check the date and resubmit."),

            Doc(networks, ExamType.Final, 6, 2024, PaperStatus.Approved, new DateTime(2024, 6, 27, 8, 51, 0, DateTimeKind.Utc)),
            Doc(networks, ExamType.Midterm, 11, 2025, PaperStatus.Pending, new DateTime(2025, 11, 30, 23, 9, 0, DateTimeKind.Utc)),

            Doc(stats, ExamType.Final, 6, 2025, PaperStatus.Approved, new DateTime(2025, 6, 12, 7, 38, 0, DateTimeKind.Utc)),

            Doc(progFund, ExamType.Final, 1, 2025, PaperStatus.Approved, new DateTime(2025, 1, 21, 11, 2, 0, DateTimeKind.Utc)),
            Doc(progFund, ExamType.Resit, 9, 2025, PaperStatus.Approved, new DateTime(2025, 9, 16, 13, 27, 0, DateTimeKind.Utc)),

            Doc(arch, ExamType.Final, 6, 2025, PaperStatus.Pending, new DateTime(2025, 6, 25, 16, 43, 0, DateTimeKind.Utc)),

            Doc(digital, ExamType.Midterm, 11, 2024, PaperStatus.Approved, new DateTime(2024, 11, 19, 9, 56, 0, DateTimeKind.Utc)));

        // Linear Algebra is intentionally left with no papers — a subject that is
        // taught but has an empty archive, which the UI will need to handle.

        await db.SaveChangesAsync();
    }

    // Databases seeded before bachelor's was 3 years still have the sample
    // Machine Learning link in year 4. SeedCatalogueAsync will not rebuild them.
    private static async Task ClampSubjectYearsToStudyLengthAsync(ExamArchiveDbContext db)
    {
        var over = await (
            from ms in db.MajorSubjects
            join major in db.Majors on ms.MajorId equals major.Id
            join studies in db.Studies on major.StudiesId equals studies.Id
            where ms.YearOfStudy > studies.YearsOfStudy
            select new { Link = ms, studies.YearsOfStudy }).ToListAsync();

        if (over.Count == 0)
        {
            return;
        }

        foreach (var row in over)
        {
            row.Link.YearOfStudy = row.YearsOfStudy;
        }

        await db.SaveChangesAsync();
    }

    // A separate pass rather than part of building the papers, so one piece of code
    // covers both an empty database and one that already has rows. Ordered by id, so
    // a paper lands on the same student every run, and idempotent because a row only
    // qualifies while its submitter is null.
    private static async Task BackfillPaperSubmittersAsync(
        ExamArchiveDbContext db,
        ILogger logger)
    {
        var unattributed = await db.Papers
            .Where(p => p.SubmittedByUserId == null)
            .OrderBy(p => p.Id)
            .ToListAsync();

        if (unattributed.Count == 0)
        {
            return;
        }

        var studentIds = await db.Users
            .Where(u => u.Role == UserRole.User)
            .OrderBy(u => u.Id)
            .Select(u => u.Id)
            .ToListAsync();

        // Guarded rather than assumed: the modulo below divides by this count.
        if (studentIds.Count == 0)
        {
            return;
        }

        for (var i = 0; i < unattributed.Count; i++)
        {
            unattributed[i].SubmittedByUserId = studentIds[i % studentIds.Count];
        }

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Attributed {Count} papers across {StudentCount} student accounts.",
            unattributed.Count,
            studentIds.Count);
    }

    // Seeding creates rows, not bytes, so without this every seeded paper lists
    // correctly and then 404s when something tries to open it.
    //
    // SizeBytes == 0 is the filter, and it is precise rather than convenient: a real
    // upload always records a real length, so a genuinely missing file from a real
    // submission is left alone. It is also what makes this idempotent.
    private static async Task WriteSampleFilesAsync(
        ExamArchiveDbContext db,
        PaperFileStorage storage,
        ILogger logger)
    {
        var pending = await db.PaperFiles
            .Include(f => f.Paper!)
                .ThenInclude(paper => paper.Subject)
            .Where(f => f.SizeBytes == 0)
            .ToListAsync();

        if (pending.Count == 0)
        {
            return;
        }

        var written = 0;

        foreach (var file in pending)
        {
            // Routed through the storage service rather than combined by hand, so a
            // path that escapes the uploads folder is refused here exactly as it
            // would be on the download path.
            if (!storage.TryResolve(file.StoredPath, out var absolutePath))
            {
                logger.LogWarning(
                    "Skipped {StoredPath}: it does not resolve inside the uploads folder.",
                    file.StoredPath);

                continue;
            }

            var paper = file.Paper!;

            var bytes = SamplePdf.Render(
            [
                "EXAM ARCHIVE - SAMPLE PAPER",
                $"{paper.Subject!.Code ?? "PAPER"} - {paper.ExamType} - {paper.Month:D2}/{paper.Year}",
                $"Page {file.PageNumber}",
            ]);

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllBytesAsync(absolutePath, bytes);

            file.SizeBytes = bytes.Length;
            written++;
        }

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Wrote {Count} placeholder paper files so seeded papers can be opened.",
            written);
    }

    // Separate from the main seed block and run unconditionally, because the database
    // that most needs these is the one already full of sample papers from before
    // accounts existed — the Majors.AnyAsync early return would skip it.
    private static async Task SeedUsersAsync(
        ExamArchiveDbContext db,
        UserAccountService accounts,
        ILogger logger)
    {
        var usernames = DevAccounts.Select(account => account.Username).ToList();

        // Matched by the column's case-insensitive collation, so this finds the row
        // created as "admin" rather than missing it and colliding on the unique index.
        var existing = await db.Users
            .Where(u => usernames.Contains(u.Username))
            .ToListAsync();

        var created = new List<string>();
        var reset = new List<string>();

        foreach (var (username, role) in DevAccounts)
        {
            var user = existing.FirstOrDefault(
                u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

            if (user is null)
            {
                user = new User { Username = username, CreatedAt = DateTime.UtcNow };
                db.Users.Add(user);
                created.Add($"{username} ({role})");
            }
            else
            {
                reset.Add(username);
            }

            // Rewritten every start, not only on creation, so changing DevPassword
            // changes existing accounts too and an account left deactivated or
            // mid-password-change by manual testing is restored to a known state. The
            // trade is that a password deliberately changed here does not survive a
            // restart.
            user.Role = role;
            user.IsActive = true;
            user.MustChangePassword = false;

            user.PasswordHash = accounts.HashPassword(user, DevPassword);
        }

        await db.SaveChangesAsync();

        // Warning, not information: this is the only place the password appears, and
        // it is also the signal that this build is running in Development.
        logger.LogWarning(
            "Development accounts ready with the password '{Password}' — created: {Created}; "
            + "reset: {Reset}. These exist only in the Development environment.",
            DevPassword,
            created.Count > 0 ? string.Join(", ", created) : "none",
            reset.Count > 0 ? string.Join(", ", reset) : "none");
    }

    // Development convenience, not a migration: backfilling sample data in a
    // migration would carry it into a real deployment.
    //
    // Matched on the English name because the rename migration copied the old single
    // Name column into both new ones, leaving English text sitting in NameSr.
    // Subjects the catalogue does not know are left exactly as they are.
    private static async Task BackfillSubjectCodesAsync(ExamArchiveDbContext db)
    {
        var subjects = await db.Subjects.ToListAsync();
        var changed = false;

        foreach (var subject in subjects)
        {
            // Keying only on a missing code would skip every subject that had
            // already been given one, which is precisely the set whose names still
            // need correcting.
            var match = SubjectCatalogue.FirstOrDefault(entry =>
                entry.Code == subject.Code
                || entry.NameEn == subject.NameEn
                || entry.NameEn == subject.NameSr);

            if (match.Code is null)
            {
                continue;
            }

            if (subject.Code == match.Code
                && subject.NameSr == match.NameSr
                && subject.NameEn == match.NameEn)
            {
                continue;
            }

            subject.Code = match.Code;
            subject.NameSr = match.NameSr;
            subject.NameEn = match.NameEn;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    // Detected by NameSr still reading as the English name, which is the state the
    // rename migration leaves behind. Majors have no code to key on.
    private static async Task BackfillMajorNamesAsync(ExamArchiveDbContext db)
    {
        var majors = await db.Majors.ToListAsync();
        var changed = false;

        foreach (var major in majors)
        {
            var match = MajorCatalogue.FirstOrDefault(entry => entry.NameEn == major.NameSr);

            if (match.NameSr is null)
            {
                continue;
            }

            major.NameSr = match.NameSr;
            major.NameEn = match.NameEn;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }
}
