# ErrorLensLLM

Error capture, grouping and LLM-based root cause analysis for .NET applications, with a built-in dashboard.

> Status: early development (0.1.0-alpha)

## How it works

1. The package captures unhandled exceptions in your app.
2. Personal data is masked, the message is normalized and a fingerprint is computed
   (exception type + top application frames, line numbers excluded).
3. If the fingerprint already has an analysis in the database, it is returned immediately.
4. Otherwise the failing code is fetched from your Git host (read-only token) at the deployed commit,
   and an LLM explains the cause and suggests a fix.
5. The result is stored and shown in the dashboard; developers mark it correct or wrong.

## Setup

```csharp
builder.Services.AddErrorLensLLM(builder.Configuration);

app.UseErrorLensLLM();
app.MapErrorLensLLMDashboard("/errors").RequireAuthorization();
```

`appsettings.json`:

```json
{
  "ConnectionStrings": { "ErrorLensLLM": "Host=...;Database=errorlens;Username=...;Password=..." },
  "ErrorLensLLM": { "MasterKey": "..." }
}
```

AI provider, model, API key and repository tokens are configured from the dashboard (Settings) and stored encrypted.

Deploy portable PDBs with your application, otherwise stack traces have no line numbers.

## Packages

| Package | Purpose |
|---|---|
| `ErrorLensLLM.Core` | Capture model, masking, normalization, fingerprinting, background queue |
| `ErrorLensLLM.AspNetCore` | Middleware, DI setup, embedded dashboard |
| `ErrorLensLLM.Storage.PostgreSql` | PostgreSQL storage |
| `ErrorLensLLM.Source.Bitbucket` | Reads source code from Bitbucket |
| `ErrorLensLLM.AI.Claude` | Claude analyzer |
