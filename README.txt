# String Exam Portal API (SQLite)

Steps to run:

1. Open **ExamPortalApi.csproj** in Visual Studio 2022 or run from CLI:

   ```bash
   cd ExamPortalApi
   dotnet restore
   dotnet run
   ```

2. API will run on `https://localhost:5001` or `http://localhost:5000` (or given by Kestrel).

3. Endpoints:

   - `GET /api/exams/INT-2025-001` → exam + 40 questions
   - `POST /api/exams/INT-2025-001/submit` → submit answers
   - `GET /api/admin/exams/INT-2025-001/attempts?key=string-admin-2025` → all attempts (with pass/fail)

Database file: `exams.db` in the project folder (SQLite).
