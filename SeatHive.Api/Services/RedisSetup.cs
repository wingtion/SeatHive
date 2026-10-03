using StackExchange.Redis;

namespace SeatHive.Api.Services
{
    public static class RedisSetup
    {
        // Redis holds the seat locks only, and a hold goes on without its lock when Redis cannot be asked
        // (the database decides who gets a seat). So a request never waits long for Redis:
        // - the API starts while Redis is down, and keeps reconnecting in the background;
        // - while there is no connection, a command fails at once instead of waiting in a queue for one;
        // - a command that gets no answer gives up after a second (this replaces an asyncTimeout in the connection string).
        public static ConfigurationOptions CreateOptions(string connectionString)
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            options.BacklogPolicy = BacklogPolicy.FailFast;
            options.AsyncTimeout = 1000;
            return options;
        }
    }
}
