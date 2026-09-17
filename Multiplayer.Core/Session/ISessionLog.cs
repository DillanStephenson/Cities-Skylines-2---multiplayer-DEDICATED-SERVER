namespace Multiplayer.Core.Session
{
    /// <summary>The core has no logging dependency; the mod adapts this to Colossal.Logging, tests to the console.</summary>
    public interface ISessionLog
    {
        void Info(string message);

        void Warn(string message);

        void Error(string message);
    }

    public sealed class NullSessionLog : ISessionLog
    {
        public static readonly NullSessionLog Instance = new NullSessionLog();

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }
    }
}
