namespace Convey.MessageBrokers.RabbitMQ
{
    public class RabbitMqOptions
    {
        public int Retries { get; set; }
        public int RetryInterval { get; set; }
        public string HostName { get; set; }
        public int Port { get; set; }
        public string VirtualHost { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public int RequestedConnectionTimeout { get; set; } = 30000;
        public int SocketReadTimeout { get; set; } = 30000;
        public int SocketWriteTimeout { get; set; } = 30000;
        public ushort RequestedChannelMax { get; set; }
        public uint RequestedFrameMax { get; set; }
        public ushort RequestedHeartbeat { get; set; }
        public bool UseBackgroundThreadsForIO { get; set; }
        public string ConventionsCasing { get; set; }
        public ContextOptions Context { get; set; }
        public ExchangeOptions Exchange { get; set; }
        public MessageProcessorOptions MessageProcessor { get; set; }
        public SslOptions Ssl { get; set; }

        public class ContextOptions
        {
            public bool Enabled { get; set; }
            public string Header { get; set; }
            public bool IncludeCorrelationId { get; set; }
        }

        public class ExchangeOptions
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public bool Declare { get; set; }
            public bool Durable { get; set; }
            public bool AutoDelete { get; set; }
        }

        public class MessageProcessorOptions
        {
            public bool Enabled { get; set; }
            public string Type { get; set; }
            public int MessageExpirySeconds { get; set; }
        }

        public class SslOptions
        {
            public bool Enabled { get; set; }
            public string ServerName { get; set; }
            public string CertificatePath { get; set; }
        }
    }
}