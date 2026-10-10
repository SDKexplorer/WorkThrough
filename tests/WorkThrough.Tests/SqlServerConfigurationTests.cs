using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class SqlServerConfigurationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("master")]
    [InlineData("MODEL")]
    [InlineData("msdb")]
    [InlineData("tempdb")]
    public void Configuration_rejects_missing_or_system_database_before_opening_a_connection(string database)
    {
        Assert.Throws<ArgumentException>(() => new SqlServerJobStore($"Server=unreachable.invalid;Database={database};Integrated Security=true"));
    }
}
