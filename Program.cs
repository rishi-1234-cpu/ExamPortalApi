using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

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
            SubmittedAtUtc = DateTime.UtcNow
        };

        db.Results.Add(entity);
        db.SaveChanges();

        return entity.ToDto();
    }
}

// Seeder
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

        // =========================
        // C Language – 5 questions
        // =========================
        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "C Language",
            Text = "What is the size of int on a typical 32-bit system?",
            OptionsJson = JsonSerializer.Serialize(new[] { "1 byte", "2 bytes", "4 bytes", "8 bytes" }),
            CorrectIndex = 2,
            Marks = 1
        });

        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "C Language",
            Text = "Which of the following is a valid declaration of main in C?",
            OptionsJson = JsonSerializer.Serialize(new[]
            {
                "int main()", "void main()", "main()", "integer main()"
            }),
            CorrectIndex = 0,
            Marks = 1
        });

        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "C Language",
            Text = "What is the output of: printf(\"%d\", 5/2);",
            OptionsJson = JsonSerializer.Serialize(new[] { "2.5", "2", "3", "Error" }),
            CorrectIndex = 1,
            Marks = 1
        });

        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "C Language",
            Text = "Which keyword is used to define a constant in C?",
            OptionsJson = JsonSerializer.Serialize(new[] { "#define", "const", "static", "final" }),
            CorrectIndex = 1,
            Marks = 1
        });

        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "C Language",
            Text = "Which of these is a valid storage class in C?",
            OptionsJson = JsonSerializer.Serialize(new[] { "auto", "value", "managed", "private" }),
            CorrectIndex = 0,
            Marks = 1
        });

        // =========================
        // Data Structures – 5 questions
        // =========================
        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "Data Structures",
            Text = "Which data structure works on FIFO principle?",
            OptionsJson = JsonSerializer.Serialize(new[] { "Stack", "Queue", "Tree", "Graph" }),
            CorrectIndex = 1,
            Marks = 1
        });

        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "Data Structures",
            Text = "Which traversal of a BST gives sorted order of elements?",
            OptionsJson = JsonSerializer.Serialize(new[] { "Preorder", "Inorder", "Postorder", "Level order" }),
            CorrectIndex = 1,
            Marks = 1
        });

        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "Data Structures",
            Text = "Time complexity of binary search on a sorted array is:",
            OptionsJson = JsonSerializer.Serialize(new[] { "O(n)", "O(log n)", "O(n log n)", "O(1)" }),
            CorrectIndex = 1,
            Marks = 1
        });

        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "Data Structures",
            Text = "Which data structure is most suitable for implementing recursion?",
            OptionsJson = JsonSerializer.Serialize(new[] { "Queue", "Array", "Stack", "Linked List" }),
            CorrectIndex = 2,
            Marks = 1
        });

        questions.Add(new QuestionEntity
        {
            Id = id++,
            Section = "Data Structures",
            Text = "Which of the following is a self-balancing binary search tree?",
            OptionsJson = JsonSerializer.Serialize(new[] { "Binary Heap", "AVL Tree", "Graph", "Trie" }),
            CorrectIndex = 1,
            Marks = 1
        });

        // =========================
        // Aptitude – 10 questions
        // =========================
        void AddApt(string text, string[] opts, int corr)
        {
            questions.Add(new QuestionEntity
            {
                Id = id++,
                Section = "Aptitude",
                Text = text,
                OptionsJson = JsonSerializer.Serialize(opts),
                CorrectIndex = corr,
                Marks = 1
            });
        }

        AddApt("Find the missing number: 3, 8, 15, 24, 35, __",
            new[] { "46", "48", "49", "50" }, 0);

        AddApt("A train 180m long crosses a pole in 12s. What is its approximate speed (km/h)?",
            new[] { "35", "45", "54", "60" }, 2);

        AddApt("The ratio of two numbers is 3:5. If their sum is 80, what is the larger number?",
            new[] { "30", "50", "48", "20" }, 1);

        AddApt("Simplify: 48 ÷ 2 (9 + 3)",
            new[] { "2", "24", "288", "12" }, 2);

        AddApt("If A is 25% more than B, then B is how much less than A?",
            new[] { "15%", "20%", "25%", "30%" }, 1);

        AddApt("Find the odd one out: 36, 49, 25, 22",
            new[] { "36", "49", "25", "22" }, 3);

        AddApt("2 men or 3 women can do a work in 10 days. In how many days will 6 women finish it?",
            new[] { "5 days", "10 days", "7 days", "6 days" }, 0);

        AddApt("What is 15% of 240?",
            new[] { "24", "30", "36", "40" }, 2);

        AddApt("Series: 2, 4, 12, 48, __",
            new[] { "96", "132", "192", "240" }, 2);

        AddApt("Cost Price is ₹500, Profit is 20%. Selling Price?",
            new[] { "550", "600", "520", "700" }, 1);

        // =========================
        // SQL – 5 questions
        // =========================
        void AddSql(string text, string[] opts, int corr)
        {
            questions.Add(new QuestionEntity
            {
                Id = id++,
                Section = "SQL",
                Text = text,
                OptionsJson = JsonSerializer.Serialize(opts),
                CorrectIndex = corr,
                Marks = 1
            });
        }

        AddSql("PRIMARY KEY ensures:",
            new[] { "Duplicates", "Nulls allowed", "Unique & Not Null", "None" }, 2);

        AddSql("Which query gives 2nd highest salary?",
            new[]
            {
                "SELECT TOP 2 salary FROM Employee ORDER BY salary DESC",
                "SELECT MAX(salary) FROM Employee",
                "SELECT MAX(salary) FROM Employee WHERE salary < (SELECT MAX(salary) FROM Employee)",
                "SELECT salary FROM Employee"
            }, 2);

        AddSql("Index mainly improves:",
            new[] { "Insert performance", "Delete performance", "Read/query performance", "Disk space" }, 2);

        AddSql("JOIN is used to:",
            new[] { "Add data", "Combine rows from multiple tables", "Delete data", "None" }, 1);

        AddSql("Which prevents SQL Injection?",
            new[] { "String concatenation", "Dynamic SQL", "Parameterized queries", "None" }, 2);

        // =========================
        // HTML/CSS/JS/React – 15 questions
        // =========================
        void AddTech(string section, string text, string[] opts, int corr)
        {
            questions.Add(new QuestionEntity
            {
                Id = id++,
                Section = section,
                Text = text,
                OptionsJson = JsonSerializer.Serialize(opts),
                CorrectIndex = corr,
                Marks = 1
            });
        }

        // HTML/CSS (4)
        AddTech("HTML/CSS", "Which tag is used to create a hyperlink in HTML?",
            new[] { "<a>", "<link>", "<href>", "<url>" }, 0);

        AddTech("HTML/CSS", "Which CSS property changes the text color?",
            new[] { "font-style", "text-color", "color", "text-style" }, 2);

        AddTech("HTML/CSS", "Which meta tag helps for responsive design?",
            new[]
            {
                "<meta charset=\"utf-8\">",
                "<meta viewport=\"device\">",
                "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
                "<meta responsive>"
            }, 2);

        AddTech("HTML/CSS", "Which HTML tag is used to create an ordered list?",
            new[] { "<ul>", "<ol>", "<li>", "<list>" }, 1);

        // JavaScript (5)
        AddTech("JavaScript", "Which method is used to parse a JSON string?",
            new[] { "JSON.decode()", "JSON.parse()", "JSON.toObject()", "JSON.stringify()" }, 1);

        AddTech("JavaScript", "Which keyword declares a block-scoped variable?",
            new[] { "var", "let", "static", "global" }, 1);

        AddTech("JavaScript", "How do you write an arrow function?",
            new[]
            {
                "function() => {}",
                "() => {}",
                "=> function() {}",
                "func => {}"
            }, 1);

        AddTech("JavaScript", "Which comparison operator checks both value and type?",
            new[] { "==", "!=", "===", ">=" }, 2);

        AddTech("JavaScript", "Which object is used for console logging?",
            new[] { "window", "console", "document", "log" }, 1);

        // React (6)
        AddTech("React", "Which hook is used for state in a functional component?",
            new[] { "useEffect", "useState", "useRef", "useMemo" }, 1);

        AddTech("React", "useEffect(() => {}, []) runs:",
            new[] { "On every render", "Only on first render", "On unmount only", "Never" }, 1);

        AddTech("React", "Keys in list rendering should be:",
            new[] { "Random", "Index always", "Unique & stable", "Optional" }, 2);

        AddTech("React", "What is JSX?",
            new[]
            {
                "A CSS preprocessor",
                "A JavaScript XML-like syntax used in React",
                "A database query language",
                "A routing library"
            }, 1);

        AddTech("React", "Which command creates a new React app (CRA)?",
            new[]
            {
                "npx create-react-app my-app",
                "npm new react-app",
                "react-cli new",
                "dotnet new react"
            }, 0);

        AddTech("React", "Which hook is best for side effects (API calls, subscriptions)?",
            new[] { "useState", "useEffect", "useMemo", "useCallback" }, 1);

        // Attach questions and save
        exam.Questions = questions;
        db.Exams.Add(exam);
        db.SaveChanges();
    }
}

#endregion
