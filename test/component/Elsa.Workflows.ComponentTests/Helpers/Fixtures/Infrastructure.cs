using DotNet.Testcontainers.Builders;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Alterations;
using Elsa.Persistence.EFCore.Modules.Identity;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.MsSql;
using Testcontainers.Oracle;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Elsa.Workflows.ComponentTests.Fixtures;

public class Infrastructure : IAsyncLifetime
{
    // https://xuanyuan.cloud/r/gvenzl/oracle-free
    public readonly OracleContainer DbContainer = new OracleBuilder()
        .WithImage("gvenzl/oracle-free:slim-faststart")
        .WithPortBinding(1521, 1521)
        .WithEnvironment("ORACLE_PASSWORD", "123456")
        .WithEnvironment("APP_USER", "ELSA")
        .WithEnvironment("APP_USER_PASSWORD", "123456")
        .Build();

    public readonly RabbitMqContainer RabbitMqContainer = new RabbitMqBuilder()
        .WithImage("rabbitmq:4-management")
        .Build();

    /// <summary>  
    /// 使用 Elsa 用户的连接字符串，供 WorkflowServer 使用。  
    /// </summary>  
    public string ElsaConnectionString { get; private set; } = "Data Source=192.168.83.255:1521/FREEPDB1;User ID=ELSA;PASSWORD=123456";

    public async Task InitializeAsync()
    {
        ElsaDbContextBase.ElsaSchema = "ELSA";

        await Task.WhenAll(
            DbContainer.StartAsync(),
            RabbitMqContainer.StartAsync());


        //await EnsureElsaUserAsync();
        await RunMigrationsAsync();
    }

    private async Task EnsureElsaUserAsync()
    {
        // 用 system 用户连接，创建 Elsa 用户  
        //var systemConnStr = DbContainer.GetConnectionString().Replace("SERVICE_NAME=XE", "SERVICE_NAME=FREEPDB1");
        var systemConnStr = "Data Source=192.168.83.255:1521/FREEPDB1;User ID=SYS;PASSWORD=123456;DBA Privilege=SYSDBA";
        await using var conn = new OracleConnection(systemConnStr);

        await conn.OpenAsync();

        // 检查用户是否已存在（WithReuse 时容器复用，用户可能已存在）  
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText =
            $"SELECT COUNT(*) FROM all_users WHERE username = '{ElsaUser.ToUpperInvariant()}'";
        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

        if (!exists)
        {
            await ExecuteAsync(conn, $"""  
                CREATE USER "{ElsaUser}"  
                    IDENTIFIED BY "{ElsaPassword}"  
                    DEFAULT TABLESPACE USERS  
                    TEMPORARY TABLESPACE TEMP  
                    QUOTA UNLIMITED ON USERS  
                """);

            await ExecuteAsync(conn, $"""  
                GRANT CONNECT, RESOURCE, CREATE SESSION,  
                      CREATE TABLE, CREATE SEQUENCE, CREATE VIEW,  
                      CREATE PROCEDURE, CREATE TRIGGER,  
                      UNLIMITED TABLESPACE  
                TO "{ElsaUser}"  
                """);
        }
    }

    private static async Task ExecuteAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RunMigrationsAsync()
    {
        await MigrateAsync<ManagementElsaDbContext>();
        await MigrateAsync<RuntimeElsaDbContext>();
        await MigrateAsync<IdentityElsaDbContext>();
        await MigrateAsync<AlterationsElsaDbContext>();
    }

    private async Task MigrateAsync<TDbContext>() where TDbContext : ElsaDbContextBase
    {
        var optionsBuilder = new DbContextOptionsBuilder<TDbContext>(); // 必须是泛型版本  
        optionsBuilder.UseElsaOracle(
            typeof(OracleProvidersExtensions).Assembly,
            ElsaConnectionString,
            new ElsaDbContextOptions().Configure());

        // ElsaDbContextBase 构造函数需要 IServiceProvider，提供一个空的即可  
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        await using var dbContext = (TDbContext)ActivatorUtilities.CreateInstance(
            serviceProvider, typeof(TDbContext), optionsBuilder.Options);

        await dbContext.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        return Task.WhenAll(
            DbContainer.StopAsync(),
            RabbitMqContainer.StopAsync());
    }
}