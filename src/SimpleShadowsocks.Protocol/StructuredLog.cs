namespace SimpleShadowsocks.Protocol;

public static class StructuredLog
{
    public static void Info(string component, string protocol, string message, uint? sessionId = null)
    {
        Write("INFO", component, protocol, message, sessionId, null);
    }

    public static void Warn(string component, string protocol, string message, uint? sessionId = null)
    {
        Write("WARN", component, protocol, message, sessionId, null);
    }

    public static void Error(string component, string protocol, string message, Exception exception, uint? sessionId = null)
    {
        Write("ERROR", component, protocol, message, sessionId, exception);
    }

    private static void Write(
        string level,
        string component,
        string protocol,
        string message,
        uint? sessionId,
        Exception? exception)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var sessionText = sessionId?.ToString() ?? "-";
        var sanitizedMessage = Sanitize(message);
        if (exception is null)
        {
            Console.WriteLine(
                $"{timestamp} level={level} component={component} protocol={protocol} session={sessionText} msg=\"{sanitizedMessage}\"");
            return;
        }

        var errorType = exception.GetType().Name;
        var errorMessage = Sanitize(exception.Message);
        Console.WriteLine(
            $"{timestamp} level={level} component={component} protocol={protocol} session={sessionText} msg=\"{sanitizedMessage}\" error_type={errorType} error=\"{errorMessage}\"");
    }

    private static string Sanitize(string value)
    {
        return value.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
    }
}
