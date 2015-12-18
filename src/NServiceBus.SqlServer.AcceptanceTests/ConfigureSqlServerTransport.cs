﻿using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using NServiceBus;
using NServiceBus.AcceptanceTesting.Support;
using NServiceBus.Configuration.AdvanceExtensibility;
using NServiceBus.Transports;

public class ConfigureSqlServerTransport : IConfigureTestExecution
{
    BusConfiguration busConfiguration;
    string connectionString;

    public Task Configure(BusConfiguration configuration, IDictionary<string, string> settings)
    {
        busConfiguration = configuration;
        connectionString = settings["Transport.ConnectionString"];
        configuration.UseTransport<SqlServerTransport>().ConnectionString(connectionString);
        return Task.FromResult(0);
    }

    public Task Cleanup()
    {
        var bindings = busConfiguration.GetSettings().Get<QueueBindings>();
        var queueNames = new List<string>();

        using (var conn = new SqlConnection(connectionString))
        {
            conn.Open();

            var qn = bindings.ReceivingAddresses.ToList().ToList();
            qn.ForEach(n =>
            {
                var nameParts = n.Split('@');
                if (nameParts.Length == 2)
                    queueNames.Add($"[{nameParts[1]}].[{nameParts[0]}]");
                else
                    queueNames.Add(n);
            });
            foreach (var queue in queueNames)
            {
                using (var comm = conn.CreateCommand())
                {
                    comm.CommandText = $"IF OBJECT_ID('{queue}', 'U') IS NOT NULL DROP TABLE {queue}";
                    comm.ExecuteNonQuery();
                }
            }
        }

        return Task.FromResult(0);
    }
}
