﻿namespace NServiceBus.Transport.SQLServer
{
    using System;
    using System.Collections.Generic;
    using System.Data.SqlClient;
    using System.Threading.Tasks;
    using Performance.TimeToBeReceived;
    using Routing;
    using Settings;
    using Transport;

    class LegacySqlServerTransportInfrastructure : TransportInfrastructure
    {
        public LegacySqlServerTransportInfrastructure(LegacyQueueAddressTranslator addressTranslator, SettingsHolder settings)
        {
            this.addressTranslator = addressTranslator;
            this.settings = settings;
            schemaAndCatalogSettings = settings.GetOrCreate<EndpointSchemaAndCatalogSettings>();
        }

        public override IEnumerable<Type> DeliveryConstraints { get; } = new[]
        {
            typeof(DiscardIfNotReceivedBefore)
        };

        public override TransportTransactionMode TransactionMode { get; } = TransportTransactionMode.TransactionScope;

        public override OutboundRoutingPolicy OutboundRoutingPolicy { get; } = new OutboundRoutingPolicy(OutboundRoutingType.Unicast, OutboundRoutingType.Unicast, OutboundRoutingType.Unicast);

        LegacySqlConnectionFactory CreateLegacyConnectionFactory()
        {
            var factory = settings.Get<Func<string, Task<SqlConnection>>>(SettingsKeys.LegacyMultiInstanceConnectionFactory);

            return new LegacySqlConnectionFactory(factory);
        }

        public override TransportReceiveInfrastructure ConfigureReceiveInfrastructure()
        {
            if (!settings.TryGet(out QueuePeekerOptions peekerOptions))
            {
                peekerOptions = new QueuePeekerOptions();
            }

            var connectionFactory = CreateLegacyConnectionFactory();

            var queuePurger = new LegacyQueuePurger(connectionFactory);
            var queuePeeker = new LegacyQueuePeeker(connectionFactory, peekerOptions);

            var expiredMessagesPurger = CreateExpiredMessagesPurger(connectionFactory);
            var schemaVerification = new SchemaInspector(queue => connectionFactory.OpenNewConnection(queue.Name));

            if (!settings.TryGet(out SqlScopeOptions scopeOptions))
            {
                scopeOptions = new SqlScopeOptions();
            }

            if (!settings.TryGet(SettingsKeys.TimeToWaitBeforeTriggering, out TimeSpan waitTimeCircuitBreaker))
            {
                waitTimeCircuitBreaker = TimeSpan.FromSeconds(30);
            }

            Func<TransportTransactionMode, ReceiveStrategy> receiveStrategyFactory =
                guarantee =>
                {
                    if (guarantee != TransportTransactionMode.TransactionScope)
                    {
                        throw new Exception("Legacy multiinstance mode is supported only with TransportTransactionMode=TransactionScope");
                    }

                    return new LegacyProcessWithTransactionScope(scopeOptions.TransactionOptions, connectionFactory, new FailureInfoStorage(1000));
                };

            Func<string, TableBasedQueue> queueFactory = x => new TableBasedQueue(addressTranslator.Parse(x).QualifiedTableName, x);

            return new TransportReceiveInfrastructure(
                () => new MessagePump(receiveStrategyFactory, queueFactory, queuePurger, expiredMessagesPurger, queuePeeker, schemaVerification, waitTimeCircuitBreaker),
                () => new LegacyQueueCreator(connectionFactory, addressTranslator),
                () => Task.FromResult(StartupCheckResult.Success));
        }

        ExpiredMessagesPurger CreateExpiredMessagesPurger(LegacySqlConnectionFactory connectionFactory)
        {
            var purgeTaskDelay = settings.HasSetting(SettingsKeys.PurgeTaskDelayTimeSpanKey) ? settings.Get<TimeSpan?>(SettingsKeys.PurgeTaskDelayTimeSpanKey) : null;
            var purgeBatchSize = settings.HasSetting(SettingsKeys.PurgeBatchSizeKey) ? settings.Get<int?>(SettingsKeys.PurgeBatchSizeKey) : null;

            return new ExpiredMessagesPurger(queue => connectionFactory.OpenNewConnection(queue.Name), purgeTaskDelay, purgeBatchSize);
        }

        public override TransportSendInfrastructure ConfigureSendInfrastructure()
        {
            var connectionFactory = CreateLegacyConnectionFactory();

            settings.GetOrCreate<EndpointInstances>().AddOrReplaceInstances("SqlServer", schemaAndCatalogSettings.ToEndpointInstances());
            return new TransportSendInfrastructure(
                () => new LegacyMessageDispatcher(addressTranslator, connectionFactory),
                () => Task.FromResult(StartupCheckResult.Success));
        }

        public override TransportSubscriptionInfrastructure ConfigureSubscriptionInfrastructure()
        {
            throw new NotImplementedException();
        }

        public override EndpointInstance BindToLocalEndpoint(EndpointInstance instance)
        {
            var schemaSettings = settings.Get<EndpointSchemaAndCatalogSettings>();

            if (schemaSettings.TryGet(instance.Endpoint, out var schema) == false)
            {
                schema = addressTranslator.DefaultSchema;
            }

            var result = instance.SetProperty(SettingsKeys.SchemaPropertyKey, schema);
            return result;
        }

        public override string ToTransportAddress(LogicalAddress logicalAddress)
        {
            return addressTranslator.Generate(logicalAddress).Value;
        }

        public override string MakeCanonicalForm(string transportAddress)
        {
            return addressTranslator.Parse(transportAddress).Address;
        }

        LegacyQueueAddressTranslator addressTranslator;
        SettingsHolder settings;
        EndpointSchemaAndCatalogSettings schemaAndCatalogSettings;
    }
}