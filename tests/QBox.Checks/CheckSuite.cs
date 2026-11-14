namespace QBox.Checks;

internal sealed class CheckSuite
{
    public int Total { get; private set; }
    public int Failed { get; private set; }

    public void Run(string name, Action action)
    {
        Total++;
        try { action(); }
        catch (Exception error)
        {
            Failed++;
            if (Failed <= 25) Console.Error.WriteLine($"FAIL {name}: {error}");
        }
    }

    public async Task RunAsync(string name, Func<Task> action)
    {
        Total++;
        try { await action(); }
        catch (Exception error)
        {
            Failed++;
            if (Failed <= 25) Console.Error.WriteLine($"FAIL {name}: {error}");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected={expected}, actual={actual}");
    }

    public static void True(bool value, string message = "条件不成立")
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"没有抛出 {typeof(T).Name}");
    }
}
