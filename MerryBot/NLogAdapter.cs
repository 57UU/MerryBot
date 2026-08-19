using CommonLib;

namespace MerryBot;

internal class NLogAdapter : ISimpleLogger
{
    private readonly NLog.Logger _logger;

    /// <param name="loggerName">NLog logger 名；默认 "NapcatClient" 保持既有行为。</param>
    public NLogAdapter(string loggerName = "NapcatClient")
    {
        _logger = NLog.LogManager.GetLogger(loggerName);
    }

    public void Debug(string message)
    {
        _logger.Debug(message);
    }

    public void Error(string message)
    {
        _logger.Error(message);
    }

    public void Fatal(string message)
    {
        _logger.Fatal(message);
    }

    public void Info(string message)
    {
        _logger.Info(message);
    }

    public void Trace(string message)
    {
        _logger.Trace(message);
    }

    public void Warn(string message)
    {
        _logger.Warn(message);
    }
}
