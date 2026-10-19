namespace Candela.App.Diagnostics;

/// <summary>
/// Decides how an exception caught by the frame loop is logged: the first occurrence in full (with the stack trace),
/// repeats of the same failure (same type, message and throwing method) only as a count at 10, 100, 1000 … — an error
/// that happens every frame would otherwise write megabytes of identical stack traces per minute.
/// </summary>
internal sealed class RepeatedErrorFilter
{
    private Type? _type;
    private string? _message;
    private string? _method;

    /// <summary>How often the current failure happened so far.</summary>
    public long Repeats { get; private set; }

    /// <summary>
    /// Records <paramref name="exception"/>. Returns <see cref="ErrorLogKind.Full"/> for a new failure,
    /// <see cref="ErrorLogKind.Count"/> when a repeat reaches 10, 100, 1000 … occurrences and
    /// <see cref="ErrorLogKind.None"/> otherwise.
    /// </summary>
    public ErrorLogKind Record(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string? method = exception.TargetSite?.Name;
        if (exception.GetType() == _type && exception.Message == _message && method == _method)
        {
            Repeats++;
            return IsPowerOfTen(Repeats) ? ErrorLogKind.Count : ErrorLogKind.None;
        }

        _type = exception.GetType();
        _message = exception.Message;
        _method = method;
        Repeats = 1;
        return ErrorLogKind.Full;
    }

    private static bool IsPowerOfTen(long n)
    {
        if (n < 10)
            return false;
        while (n % 10 == 0)
            n /= 10;
        return n == 1;
    }
}

/// <summary>How a recorded failure is logged (see <see cref="RepeatedErrorFilter"/>).</summary>
internal enum ErrorLogKind
{
    /// <summary>Not at all (a repeat).</summary>
    None,

    /// <summary>The whole exception with its stack trace (a new failure).</summary>
    Full,

    /// <summary>One line with the number of repeats so far.</summary>
    Count,
}
