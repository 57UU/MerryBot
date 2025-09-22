namespace CommonLib;

public interface ISimpleLogger
{
    public void Trace(string message);
    public void Debug(string message);
    public void Info(string message);
    public void Warn(string message);
    public void Error(string message);
    public void Fatal(string message);
}
public class ConsoleLogger : ISimpleLogger
{
    private ConsoleLogger() { }
    public static ConsoleLogger Instance { get; } = new ConsoleLogger();
    public void Debug(string message)
    {
        Console.WriteLine($"Debug:{message}");
    }

    public void Error(string message)
    {
        Console.WriteLine($"Error:{message}");
    }

    public void Fatal(string message)
    {
        Console.WriteLine($"Fatal:${message}");
    }

    public void Info(string message)
    {
        Console.WriteLine($"Info:{message}");
    }

    public void Trace(string message)
    {
        Console.WriteLine($"Trace:{message}");
    }

    public void Warn(string message)
    {
        Console.WriteLine($"Warn:{message}");
    }
}