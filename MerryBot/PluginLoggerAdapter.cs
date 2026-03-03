using CommonLib;

namespace MerryBot;

class PluginLogger(string tag) : ISimpleLogger
{
    private readonly NLog.Logger _logger = NLog.LogManager.GetLogger($"plugin:{tag}");

    public void Debug(string message)
    {
        _logger.Debug(message);
    }
    public void Trace(string message)
    {
        _logger.Trace(message);
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

    public void Warn(string message)
    {
        _logger.Warn(message);
    }
}
