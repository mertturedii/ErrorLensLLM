namespace ErrorLensLLM.Core.Incidents;

/// <summary>Rule-based category from the exception type and message; the baseline for learned classifiers.</summary>
public static class ErrorCategorizer
{
    public static ErrorCategory Categorize(string exceptionType, string message)
    {
        var type = exceptionType;
        var text = message.ToLowerInvariant();

        if (IsDatabase(type))
        {
            // PostgreSQL / SQL Server wording for objects the code expects but the schema lacks
            return text.Contains("does not exist") || text.Contains("invalid column") || text.Contains("invalid object name")
                ? ErrorCategory.DatabaseSchema
                : ErrorCategory.Database;
        }

        if (type.EndsWith("TimeoutException") || type.EndsWith("TaskCanceledException") || text.Contains("timed out") || text.Contains("timeout"))
            return ErrorCategory.Timeout;

        if (type.EndsWith("HttpRequestException") || type.EndsWith("SocketException") || text.Contains("connection refused"))
            return ErrorCategory.Dependency;

        if (type.EndsWith("UnauthorizedAccessException") || type.Contains("Authentication") || type.Contains("SecurityToken"))
            return ErrorCategory.Authorization;

        if (type.EndsWith("ValidationException") || type.EndsWith("FormatException"))
            return ErrorCategory.Validation;

        if (type.Contains("Configuration") || type.EndsWith("OptionsValidationException"))
            return ErrorCategory.Configuration;

        if (type.EndsWith("NullReferenceException") || type.EndsWith("ArgumentNullException") || type.EndsWith("InvalidCastException")
            || type.EndsWith("IndexOutOfRangeException") || type.EndsWith("ArgumentOutOfRangeException") || type.EndsWith("KeyNotFoundException"))
            return ErrorCategory.CodeDefect;

        return ErrorCategory.Unknown;
    }

    private static bool IsDatabase(string type) =>
        type.StartsWith("Npgsql.") || type.EndsWith("SqlException") || type.Contains("EntityFrameworkCore") || type.EndsWith("DbUpdateException");
}
