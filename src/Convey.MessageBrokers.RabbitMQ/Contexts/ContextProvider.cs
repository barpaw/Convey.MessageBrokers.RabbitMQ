using System.Collections.Generic;

namespace Convey.MessageBrokers.RabbitMQ.Contexts
{
    internal sealed class ContextProvider : IContextProvider
    {
        private readonly IRabbitMqSerializer _serializer;
        public string HeaderName { get; }

        public ContextProvider(IRabbitMqSerializer serializer, RabbitMqOptions options)
        {
            _serializer = serializer;
            HeaderName = string.IsNullOrWhiteSpace(options.Context?.Header)
                ? "message_context"
                : options.Context.Header;
        }

        public ICorrelationContext Get(IDictionary<string, object> headers)
        {
            if (!headers.TryGetValue(HeaderName, out var context))
            {
                return null;
            }

            if (!(context is string payload))
            {
                return null;
            }

            return _serializer.Deserialize<ICorrelationContext>(payload);
        }
    }
}