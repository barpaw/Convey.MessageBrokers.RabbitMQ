using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convey.MessageBrokers.RabbitMQ.Builders;
using Convey.MessageBrokers.RabbitMQ.Processors;
using Convey.MessageBrokers.RabbitMQ.Publishers;
using Convey.MessageBrokers.RabbitMQ.Registers;
using Convey.MessageBrokers.RabbitMQ.Subscribers;
using Convey.Persistence.Redis;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using RawRabbit;
using RawRabbit.Common;
using RawRabbit.Configuration;
using RawRabbit.Enrichers.MessageContext;
using RawRabbit.Instantiation;
using RawRabbit.Pipe;
using RawRabbit.Pipe.Middleware;
using RawRabbit.Serialization;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Convey.MessageBrokers.RabbitMQ
{
    public static class Extensions
    {
        private const string SectionName = "rabbitMq";
        private const string RegistryName = "messageBrokers.rabbitMq";

        internal static string GetMessageName(this object message)
            => message.GetType().Name.Underscore().ToLowerInvariant();

        public static IBusSubscriber UseRabbitMq(this IApplicationBuilder app)
            => new BusSubscriber(app);

        public static IConveyBuilder AddExceptionToMessageMapper<T>(this IConveyBuilder builder)
            where T : class, IExceptionToMessageMapper
        {
            builder.Services.AddTransient<IExceptionToMessageMapper, T>();

            return builder;
        }

        public static IConveyBuilder AddRabbitMq<TContext>(this IConveyBuilder builder, string sectionName = SectionName,
            string redisSectionName = "redis", Func<IRabbitMqPluginRegister, IRabbitMqPluginRegister> plugins = null)
            where TContext : ICorrelationContext, new()
        {
            var options = builder.GetOptions<RabbitMqOptions>(sectionName);
            var redisOptions = builder.GetOptions<RedisOptions>(redisSectionName);
            return builder.AddRabbitMq<TContext>(options, plugins, b => b.AddRedis(redisOptions));
        }

        public static IConveyBuilder AddRabbitMq<TContext>(this IConveyBuilder builder,
            Func<IRabbitMqOptionsBuilder, IRabbitMqOptionsBuilder> buildOptions,
            Func<IRabbitMqPluginRegister, IRabbitMqPluginRegister> plugins = null,
            Func<IRedisOptionsBuilder, IRedisOptionsBuilder> buildRedisOptions = null)
            where TContext : ICorrelationContext, new()
        {
            var options = buildOptions(new RabbitMqOptionsBuilder()).Build();
            return buildRedisOptions is null
                ? builder.AddRabbitMq<TContext>(options, plugins)
                : builder.AddRabbitMq<TContext>(options, plugins, b => b.AddRedis(buildRedisOptions));
        }

        public static IConveyBuilder AddRabbitMq<TContext>(this IConveyBuilder builder, RabbitMqOptions options,
            Func<IRabbitMqPluginRegister, IRabbitMqPluginRegister> plugins = null,
            RedisOptions redisOptions = null)
            where TContext : ICorrelationContext, new()
            => builder.AddRabbitMq<TContext>(options, plugins, b => b.AddRedis(redisOptions ?? new RedisOptions()));

        private static IConveyBuilder AddRabbitMq<TContext>(this IConveyBuilder builder, RabbitMqOptions options,
            Func<IRabbitMqPluginRegister, IRabbitMqPluginRegister> plugins, Action<IConveyBuilder> registerRedis)
            where TContext : ICorrelationContext, new()
        {
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<RawRabbitConfiguration>(options);
            if (!builder.TryRegister(RegistryName))
            {
                return builder;
            }

            builder.Services.AddTransient<IBusPublisher, BusPublisher>();
            if (options.MessageProcessor?.Enabled == true)
            {
                switch (options.MessageProcessor.Type?.ToLowerInvariant())
                {
                    case "redis":
                        registerRedis(builder);
                        builder.Services.AddTransient<IMessageProcessor, RedisMessageProcessor>();
                        break;
                    default:
                        builder.Services.AddMemoryCache();
                        builder.Services.AddTransient<IMessageProcessor, InMemoryMessageProcessor>();
                        break;
                }
            }
            else
            {
                builder.Services.AddSingleton<IMessageProcessor, EmptyMessageProcessor>();
            }

            builder.Services.AddSingleton<ICorrelationContextAccessor>(new CorrelationContextAccessor());

            ConfigureBus<TContext>(builder, plugins);

            return builder;
        }

        private static void ConfigureBus<TContext>(IConveyBuilder builder,
            Func<IRabbitMqPluginRegister, IRabbitMqPluginRegister> plugins = null)
            where TContext : ICorrelationContext, new()
        {
            builder.Services.AddSingleton<IInstanceFactory>(serviceProvider =>
            {
                var register = plugins?.Invoke(new RabbitMqPluginRegister(serviceProvider));
                var options = serviceProvider.GetService<RabbitMqOptions>();
                var configuration = serviceProvider.GetService<RawRabbitConfiguration>();
                var namingConventions = new CustomNamingConventions(options.Namespace);

                return RawRabbitFactory.CreateInstanceFactory(new RawRabbitOptions
                {
                    DependencyInjection = ioc =>
                    {
                        register?.Register(ioc);
                        ioc.AddSingleton(options);
                        ioc.AddSingleton(configuration);
                        ioc.AddSingleton(serviceProvider);
                        ioc.AddSingleton<INamingConventions>(namingConventions);
                        ioc.AddSingleton<ISerializer, Serializer<TContext>>(ctx =>
                            new Serializer<TContext>(ctx.GetService<JsonSerializer>()));
                    },
                    Plugins = p =>
                    {
                        register?.Register(p);
                        p.UseAttributeRouting()
                            .UseRetryLater()
                            .UpdateRetryInfo()
                            .UseMessageContext<TContext>()
                            .UseContextForwarding();

                        if (options.MessageProcessor?.Enabled == true)
                        {
                            p.ProcessUniqueMessages();
                        }
                    }
                });
            });

            builder.Services.AddTransient(serviceProvider => serviceProvider.GetService<IInstanceFactory>().Create());
        }

        private class CustomNamingConventions : NamingConventions
        {
            public CustomNamingConventions(string defaultNamespace)
            {
                var assemblyName = Assembly.GetEntryAssembly().GetName().Name;
                ExchangeNamingConvention = type => GetExchange(type, defaultNamespace);
                RoutingKeyConvention = type => GetRoutingKey(type, defaultNamespace);
                QueueNamingConvention = type => GetQueueName(assemblyName, type, defaultNamespace);
                ErrorExchangeNamingConvention = () => $"{defaultNamespace}.error";
                RetryLaterExchangeConvention = span => $"{defaultNamespace}.retry";
                RetryLaterQueueNameConvetion = (exchange, span) =>
                    $"{defaultNamespace}.retry_for_{exchange.Replace(".", "_")}_in_{span.TotalMilliseconds}_ms"
                        .ToLowerInvariant();
            }

            private static string GetExchange(Type type, string defaultNamespace)
            {
                var (@namespace, key) = GetNamespaceAndKey(type, defaultNamespace);

                return (string.IsNullOrWhiteSpace(@namespace) ? key : $"{@namespace}").ToLowerInvariant();
            }

            private static string GetRoutingKey(Type type, string defaultNamespace)
            {
                var (@namespace, key) = GetNamespaceAndKey(type, defaultNamespace);
                var separatedNamespace = string.IsNullOrWhiteSpace(@namespace) ? string.Empty : $"{@namespace}.";

                return $"{separatedNamespace}{key}".ToLowerInvariant();
            }

            private static string GetQueueName(string assemblyName, Type type, string defaultNamespace)
            {
                var (@namespace, key) = GetNamespaceAndKey(type, defaultNamespace);
                var separatedNamespace = string.IsNullOrWhiteSpace(@namespace) ? string.Empty : $"{@namespace}.";

                return $"{assemblyName}/{separatedNamespace}{key}".ToLowerInvariant();
            }

            private static (string @namespace, string key) GetNamespaceAndKey(Type type, string defaultNamespace)
            {
                var attribute = type.GetCustomAttribute<MessageNamespaceAttribute>();
                var @namespace = attribute?.Namespace ?? defaultNamespace;
                var key = string.IsNullOrWhiteSpace(attribute?.Key) ? type.Name.Underscore() : attribute.Key;

                return (@namespace, key);
            }
        }

        private class ProcessUniqueMessagesMiddleware : StagedMiddleware
        {
            private readonly IServiceProvider _serviceProvider;
            public override string StageMarker { get; } = RawRabbit.Pipe.StageMarker.MessageDeserialized;

            public ProcessUniqueMessagesMiddleware(IServiceProvider serviceProvider)
            {
                _serviceProvider = serviceProvider;
            }

            public override async Task InvokeAsync(IPipeContext context,
                CancellationToken token = new CancellationToken())
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var messageProcessor = scope.ServiceProvider.GetRequiredService<IMessageProcessor>();
                    var messageId = context.GetDeliveryEventArgs().BasicProperties.MessageId;
                    if (!await messageProcessor.TryProcessAsync(messageId))
                    {
                        return;
                    }

                    try
                    {
                        await Next.InvokeAsync(context, token);
                    }
                    catch
                    {
                        await messageProcessor.RemoveAsync(messageId);
                        throw;
                    }
                }
            }
        }

        private static IClientBuilder ProcessUniqueMessages(this IClientBuilder clientBuilder)
        {
            clientBuilder.Register(c => c.Use<ProcessUniqueMessagesMiddleware>());

            return clientBuilder;
        }

        private class RetryStagedMiddleware : StagedMiddleware
        {
            public override string StageMarker { get; } = RawRabbit.Pipe.StageMarker.MessageDeserialized;

            public override async Task InvokeAsync(IPipeContext context,
                CancellationToken token = new CancellationToken())
            {
                var retry = context.GetRetryInformation();
                if (context.GetMessageContext() is ICorrelationContext message)
                {
                    message.Retries = retry.NumberOfRetries;
                }

                await Next.InvokeAsync(context, token);
            }
        }

        private static IClientBuilder UpdateRetryInfo(this IClientBuilder clientBuilder)
        {
            clientBuilder.Register(c => c.Use<RetryStagedMiddleware>());

            return clientBuilder;
        }

        private class EmptyMessageProcessor : IMessageProcessor
        {
            public Task<bool> TryProcessAsync(string id) => Task.FromResult(true);

            public Task RemoveAsync(string id) => Task.CompletedTask;
        }
        
        private class Serializer<T> : RawRabbit.Serialization.JsonSerializer
        {
            public Serializer(JsonSerializer json) : base(json)
            {
                json.Converters.Add(new CorrelationContextConverter<T>());
            }
        }
        
        private class CorrelationContextConverter<T> : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return (objectType == typeof(ICorrelationContext));
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                JsonSerializer serializer)
            {
                return serializer.Deserialize(reader, typeof(T));
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                serializer.Serialize(writer, value, typeof(T));
            }
        }
    }
}