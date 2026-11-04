using Backdafuckup.Agent.Core;

namespace Backdafuckup.Agent.Tests;

[Trait("Category", "Unit")]
public sealed class AccountNamesTests
{
    [Theory]
    [InlineData(@".\Administrator", @"BDFU-LAB-SQL1\Administrator")]
    [InlineData(@"CORP\svc-backup", @"CORP\svc-backup")]
    [InlineData("svc-backup@corp.example", "svc-backup@corp.example")]
    [InlineData(@"NT SERVICE\MSSQL$SQLEXPRESS", @"NT SERVICE\MSSQL$SQLEXPRESS")]
    [InlineData("Administrator", "Administrator")]
    public void A_local_account_written_with_a_dot_gets_the_machine_name(string account, string expected) =>
        Assert.Equal(expected, AccountNames.Qualify(account, "BDFU-LAB-SQL1"));
}
