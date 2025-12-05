using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// CORS - allow all (for now)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .SetIsOriginAllowed(_ => true)
            .AllowCredentials();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// SQLite DbContext
builder.Services.AddDbContext<ExamDbContext>(options =>
{
    options.UseSqlite("Data Source=exams.db");
});

var app = builder.Build();

app.UseCors("AllowAll");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Ensure DB + Seed
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ExamDbContext>();
    db.Database.EnsureCreated();
    ExamSeeder.SeedDefaultExam(db);
}

app.MapGet("/", () => Results.Ok(new { status = "String Exam API running", db = "sqlite" }));

// Get exam info + questions by code
app.MapGet("/api/exams/{code}", async (string code, ExamDbContext db) =>
{
    var exam = await db.Exams
        .Include(e => e.Questions)
        .FirstOrDefaultAsync(e => e.Code.ToLower() == code.ToLower());

    if (exam is null) return Results.NotFound();

    var dto = exam.ToDto();
    return Results.Ok(dto);
});

// Submit exam attempt
app.MapPost("/api/exams/{code}/submit", async (string code, HttpRequest request, ExamDbContext db) =>
{
    var exam = await db.Exams
        .Include(e => e.Questions)
        .FirstOrDefaultAsync(e => e.Code.ToLower() == code.ToLower());

    if (exam is null)
    {
        return Results.NotFound(new { error = "Exam not found" });
    }

    var submitDto = await JsonSerializer.DeserializeAsync<ExamSubmitDto>(
        request.Body,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
    );

    if (submitDto is null || string.IsNullOrWhiteSpace(submitDto.StudentName))
    {
        return Results.BadRequest(new { error = "Invalid submission" });
    }

    var result = ExamEvaluator.EvaluateAttempt(exam, submitDto, db);
    return Results.Ok(result);
});

// Admin: get all attempts for an exam (simple admin key)
app.MapGet("/api/admin/exams/{code}/attempts", async (string code, HttpRequest request, ExamDbContext db) =>
{
    var key = request.Query["key"].ToString();
    if (key != ExamConstants.AdminKey)
    {
        return Results.Unauthorized();
    }

    var attempts = await db.Results
        .Where(a => a.ExamCode.ToLower() == code.ToLower())
        .OrderByDescending(a => a.SubmittedAtUtc)
        .ToListAsync();

    var dto = attempts.Select(a => a.ToDto()).ToList();
    return Results.Ok(dto);
});

app.Run();

#region Models & DbContext

public static class ExamConstants
{
    public const string AdminKey = "string-admin-2025";
}

public class ExamEntity
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int DurationMinutes { get; set; }
    public int PassingPercentage { get; set; }

    public List<QuestionEntity> Questions { get; set; } = new();
}

public class QuestionEntity
{
    public int Id { get; set; }
    public int ExamId { get; set; }
    public string Section { get; set; } = "";
    public string Text { get; set; } = "";
    public string OptionsJson { get; set; } = "[]";
    public int CorrectIndex { get; set; }
    public int Marks { get; set; }

    public ExamEntity? Exam { get; set; }
}

public class ExamResultEntity
{
    public int Id { get; set; }
    public string ExamCode { get; set; } = "";
    public string StudentName { get; set; } = "";
    public string Email { get; set; } = "";
    public string College { get; set; } = "";
    public int Score { get; set; }
    public int TotalMarks { get; set; }
    public double Percentage { get; set; }
    public bool Passed { get; set; }

    // Local system time (IST on your machine)
    public DateTime SubmittedAtUtc { get; set; }
}

public class ExamDbContext : DbContext
{
    public ExamDbContext(DbContextOptions<ExamDbContext> options) : base(options) { }

    public DbSet<ExamEntity> Exams => Set<ExamEntity>();
    public DbSet<QuestionEntity> Questions => Set<QuestionEntity>();
    public DbSet<ExamResultEntity> Results => Set<ExamResultEntity>();
}

// DTOs (API models)
public record ExamDto(
    string Code,
    string Title,
    string Description,
    int DurationMinutes,
    int TotalQuestions,
    int PassingPercentage,
    List<QuestionDto> Questions
);

public record QuestionDto(
    int Id,
    string Section,
    string Text,
    List<string> Options,
    int CorrectIndex,
    int Marks
);

public class ExamSubmitDto
{
    public string StudentName { get; set; } = "";
    public string Email { get; set; } = "";
    public string College { get; set; } = "";
    public Dictionary<int, int> Answers { get; set; } = new(); // QuestionId -> SelectedIndex
}

public record ExamResultDto(
    string ExamCode,
    string StudentName,
    string Email,
    string College,
    int Score,
    int TotalMarks,
    double Percentage,
    bool Passed,
    DateTime SubmittedAtUtc
);

// Mappers
public static class ExamMappings
{
    public static ExamDto ToDto(this ExamEntity e)
    {
        var questions = e.Questions
            .OrderBy(q => q.Id)
            .Select(q => new QuestionDto(
                Id: q.Id,
                Section: q.Section,
                Text: q.Text,
                Options: JsonSerializer.Deserialize<List<string>>(q.OptionsJson) ?? new(),
                CorrectIndex: q.CorrectIndex,
                Marks: q.Marks
            ))
            .ToList();

        return new ExamDto(
            Code: e.Code,
            Title: e.Title,
            Description: e.Description,
            DurationMinutes: e.DurationMinutes,
            TotalQuestions: questions.Count,
            PassingPercentage: e.PassingPercentage,
            Questions: questions
        );
    }

    public static ExamResultDto ToDto(this ExamResultEntity r) =>
        new ExamResultDto(
            ExamCode: r.ExamCode,
            StudentName: r.StudentName,
            Email: r.Email,
            College: r.College,
            Score: r.Score,
            TotalMarks: r.TotalMarks,
            Percentage: r.Percentage,
            Passed: r.Passed,
            SubmittedAtUtc: r.SubmittedAtUtc
        );
}

// Evaluator
public static class ExamEvaluator
{
    public static ExamResultDto EvaluateAttempt(ExamEntity exam, ExamSubmitDto submit, ExamDbContext db)
    {
        int score = 0;
        int total = exam.Questions.Sum(q => q.Marks);

        foreach (var q in exam.Questions)
        {
            if (submit.Answers.TryGetValue(q.Id, out var selectedIndex))
            {
                if (selectedIndex == q.CorrectIndex)
                {
                    score += q.Marks;
                }
            }
        }

        var percentage = total == 0 ? 0 : (score * 100.0 / total);
        var passed = percentage >= exam.PassingPercentage;

        var entity = new ExamResultEntity
        {
            ExamCode = exam.Code,
            StudentName = submit.StudentName,
            Email = submit.Email,
            College = submit.College,
            Score = score,
            TotalMarks = total,
            Percentage = Math.Round(percentage, 2),
            Passed = passed,
            SubmittedAtUtc = DateTime.Now   // ✅ local system time
        };

        db.Results.Add(entity);
        db.SaveChanges();

        return entity.ToDto();
    }
}

// Seeder (unchanged)
public static class ExamSeeder
{
    public static void SeedDefaultExam(ExamDbContext db)
    {
        if (db.Exams.Any()) return;

        var exam = new ExamEntity
        {
            Code = "INT-2025-001",
            Title = "STRING Internship Full-Stack + Aptitude Test",
            Description = "40 Questions · 60 Minutes · Passing: 60%",
            DurationMinutes = 60,
            PassingPercentage = 60
        };

        var questions = new List<QuestionEntity>();
        int id = 1;

        // ... (poora same questions block jo tumne bheja tha, yahi rehne do)
    }
}

#endregion
